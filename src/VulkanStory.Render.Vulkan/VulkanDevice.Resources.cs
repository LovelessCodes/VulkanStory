using System;
using System.Collections.Generic;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using EnumTextureInternalFormat = VulkanStory.Contracts.TextureInternalFormat;
using EnumTexturePixelFormat = VulkanStory.Contracts.TexturePixelFormat;
using EnumFramebufferAttachment = VulkanStory.Contracts.FramebufferAttachment;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace VulkanStory.Render.Vulkan;

/// <summary>Texture, sampler, framebuffer and mesh resource operations exposed to the game integration.</summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>Protects known unallocated image workspace from new nonmovable device-local mesh blocks.</summary>
    internal void SetImageWorkspaceReserve(ulong bytes) => _context.Allocator.SetImageWorkspaceReserve(bytes);

    /// <summary>Physical memory requirement for the same single-layer color-image creation contract used by targets.</summary>
    internal ulong EstimateColorImageAllocationBytes(int width, int height, Format format)
    {
        _frames.RequireResourceLifetime();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        return _textures.EstimateColorAllocationBytes((uint)width, (uint)height, format);
    }

    /// <summary>Physical requirement for an ordinary color/depth target with the declared array layers.</summary>
    internal ulong EstimateTextureImageAllocationBytes(int width, int height, Format format, int layers = 1)
    {
        _frames.RequireResourceLifetime();
        if (width <= 0 || height <= 0 || layers <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        return _textures.EstimateTextureAllocationBytes((uint)width, (uint)height, format, (uint)layers);
    }

    /// <summary>Physical requirement for the format, mip clamp and usage chosen by storage texture creation.</summary>
    internal ulong EstimateStorageImageAllocationBytes(int width, int height, Format format, int mipLevels = 1)
    {
        _frames.RequireResourceLifetime();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        return _textures.EstimateStorageAllocationBytes((uint)width, (uint)height, format,
            (uint)Math.Max(1, mipLevels));
    }

    /// <summary>Actual VMA allocation bytes owned by a live texture, excluding block padding and borrowed images.</summary>
    internal ulong TextureAllocationBytes(int textureId) => _textures.Get(textureId)?.Allocation.Size ?? 0;

    /// <summary>Finishes recorded resource users and releases completed retirements before allocating replacement targets.</summary>
    public void CompleteReleasedResources()
    {
        _frames.RequireResourceLifetime();
        EndNativePass();
        if (_frameActive) SubmitPartial();
        else _uploads.SubmitStandalone();
        _frames.CollectCompletedRetirements();
    }
    // -------------------------------------------------------------------- textures

    /// <summary>Creates a renderer-owned 2D texture from the neutral client formats and optional initial pixels.</summary>
    public int CreateTexture2D(
        int width, int height, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels, bool generateMipmaps)
    {
        int id = _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, (int)internalFormat);

        if (pixels != IntPtr.Zero)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, BytesPerPixel(internalFormat));
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        return id;
    }

    /// <summary>Creates a renderer 2D texture from a retained raw GL internal format and raw texel bytes.</summary>
    public int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel,
        bool generateMipmaps = false)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, glInternalFormat);

        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, bytesPerPixel);

            // A chain that was asked for has to be filled here. GL's texture is
            // complete the moment glGenerateMipmap runs, but an image created
            // with levels and never blitted into keeps whatever its memory held,
            // and every sample above level 0 reads that - which looks like other
            // textures bleeding onto a surface as it turns away from the camera.
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        RenderTrace.TextureCreated(id, width, height, format, pixels, bytesPerPixel);
        return id;
    }

    /// <summary>
    /// A post-chain colour texture (framebuffer slots in
    /// <see cref="Graph.TransientAllocator.PostChainSlots" />): created in the Transient
    /// memory pool class and registered with the transient allocator. Until the frame
    /// graph binds it (<see cref="BindTransientForFrame" />) it behaves like any texture.
    /// </summary>
    public int CreateTransientTexture2D(int width, int height, EnumTextureInternalFormat internalFormat,
        int framebufferSlot)
    {
        int id = _textures.Create((uint)width, (uint)height, GlEnums.TextureFormatFrom(internalFormat),
            poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, (int)internalFormat);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>Registers (or re-tags) a texture as the transient of client framebuffer slot <paramref name="framebufferSlot" />.</summary>
    public void OptInTransient(int textureId, int framebufferSlot) => _transients.OptIn(textureId, framebufferSlot);

    /// <summary><see cref="CreateTransientTexture2D" /> with a raw GL internal format token, no pixels.</summary>
    public int CreateTransientTexture2DRaw(int width, int height, int glInternalFormat, int framebufferSlot)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, glInternalFormat);
        RenderTrace.TextureCreated(id, width, height, format, IntPtr.Zero, 0);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>
    /// Serves a texture for passes [<paramref name="firstPass" />, <paramref name="lastPass" />]
    /// of the current frame through the transient allocator and returns the texture id
    /// that backs it (itself unless aliasing is on). Call after BeginFrame, in pass order.
    /// </summary>
    public int BindTransientForFrame(int textureId, int firstPass, int lastPass) =>
        _transients.Bind(textureId, firstPass, lastPass);

    /// <summary>The transient allocator the frame graph acquires physical images from.</summary>
    internal Graph.TransientAllocator Transients => _transients;

    /// <summary>The ReadSelf copy pool. Tests only.</summary>
    internal Graph.FeedbackCopyPool ReadSelfCopiesForTests => _readSelfCopies;

    /// <summary>Forces transient aliasing on or off before Initialize (default: <c>VULKANSTORY_VULKAN_ALIAS</c>).</summary>
    internal bool? TransientAliasingOverride { get; set; }

    private int CreateReadSelfCopy(Graph.FeedbackCopyDesc desc) =>
        _textures.Create(desc.Width, desc.Height, desc.Format, layers: desc.Layers, cube: desc.Cube,
            generateMipmaps: desc.MipLevels > 1, poolClass: MemoryPoolClass.Transient);

    /// <summary>Gives the previous draw's ReadSelf copies back to the pool.</summary>
    private void ReleaseReadSelfCopies()
    {
        if (_sampledTextureOverrides.Count == 0) return;
        foreach (int copy in _sampledTextureOverrides.Values) _readSelfCopies.Release(copy);
        _sampledTextureOverrides.Clear();
    }

    /// <summary>Creates a cube texture using a raw GL internal format and explicit upload texel size.</summary>
    public int CreateTextureCubeRaw(int size, int glInternalFormat, IntPtr[] facePixels, int bytesPerPixel)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], bytesPerPixel, face);
        }
        return id;
    }

    /// <summary>Creates an array texture with the requested layer count and neutral client format.</summary>
    public int CreateTexture2DArray(
        int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat) =>
        _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), layers: (uint)layers);

    /// <summary>Uploads borrowed RGBA pixels into one layer of an owned array texture.</summary>
    /// <remarks>Inline during frame recording: the same frame's page draw samples the layer,
    /// and a batched upload would show the previous layer contents for one frame (the map
    /// atlas flicker on recycled slots).</remarks>
    public void UploadTexture2DArrayLayer(int textureId, int layer, int width, int height, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, 0, 0, 0, (uint)width, (uint)height, pixels, 4, (uint)layer, preferInline: true);
    }

    /// <summary>Uploads borrowed client pixels into a texture mip rectangle using the client pixel format.</summary>
    public void UploadTexture2D(
        int textureId, int level, int x, int y, int width, int height,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels,
            pixelFormat == EnumTexturePixelFormat.Red ? 1 : 4);
    }

    /// <summary>Records mip generation for a live texture using the renderer upload/resource path.</summary>
    public void GenerateMipmaps(int textureId)
    {
        FlushPendingClears(textureId);
        _textures.GenerateMipmaps(textureId);
    }

    /// <summary>Removes a texture ID and schedules GPU-safe release through the renderer resource owner.</summary>
    public void DeleteTexture(int textureId) => ReleaseTexture(textureId);

    /// <summary>
    /// Deletes a texture and evicts every descriptor set that names it.
    ///
    /// The eviction is the important half. The texture itself is destroyed once
    /// the Frame timeline passed every frame that could name it, but a cached set would outlive it and, once the driver
    /// reused the view handle for a new texture, be served to draws of that new
    /// texture - which is a GPU read of freed memory. The GUI re-renders its text
    /// into fresh textures constantly, so this was the loading-screen crash.
    /// </summary>
    private void ReleaseTexture(int textureId)
    {
        if (_sampledTextureOverrides.Remove(textureId, out int copy)) _readSelfCopies?.Release(copy);
        _transients?.Forget(textureId);
        _textures.RestoreBinding(textureId);
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null)
        {
            _descriptors.Release(texture.Id);
            _targets.DropPendingClears(texture);
        }
        _textures.Delete(textureId, _frames);
        // Only a delete that found something is a delete. Deleting an id twice
        // (framebuffers share a depth texture) otherwise inflated the counter
        // past the number of textures that ever existed.
        if (texture != null) VulkanStats.NoteTextureDeleted();
    }

    /// <summary>Updates a retained client texture sampling parameter by its raw GL parameter name.</summary>
    public void SetTextureParameter(int textureId, int parameterName, int value) =>
        _textures.SetParameter(textureId, parameterName, value);

    /// <summary>Updates a retained client texture sampling parameter by its raw GL parameter name.</summary>
    public void SetTextureParameter(int textureId, int parameterName, float value) =>
        _textures.SetParameter(textureId, parameterName, value);

    /// <summary>Updates the texture's retained RGBA sampler-border state.</summary>
    public void SetTextureBorderColor(int textureId, float r, float g, float b, float a) =>
        _textures.SetBorderColor(textureId, r, g, b, a);

    /// <summary>Uploads normalized-short pixel data into a texture mip rectangle.</summary>
    public void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y,
        int width, int height, short[] pixels)
    {
        FlushPendingClears(textureId);
        _textures.UploadNormalizedShorts(textureId, level, x, y, width, height, pixels);
    }

    private readonly Dictionary<int, SamplerState> _standaloneSamplers = new();
    private int _nextSamplerId = 1;

    /// <summary>Creates a standalone client sampler-state ID with nearest or linear defaults.</summary>
    public int CreateSampler(bool linear)
    {
        int id = _nextSamplerId++;
        _standaloneSamplers[id] = SamplerState.Default with
        {
            MagFilter = linear ? Filter.Linear : Filter.Nearest,
            // GenSampler uses GL_NEAREST_MIPMAP_LINEAR for both variants;
            // the flag changes magnification only. Terrain relies on this
            // override retaining the atlas mip chain at a distance.
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Linear,
            Mipmapped = true,
        };
        return id;
    }

    /// <summary>Updates a supported parameter in the standalone sampler-state table.</summary>
    public void SetSamplerParameter(int samplerId, int parameterName, float value)
    {
        if (!_standaloneSamplers.TryGetValue(samplerId, out SamplerState state)) return;

        _standaloneSamplers[samplerId] = parameterName == GlEnums.TextureLodBias
            ? state with { LodBias = value }
            : state;
    }

    /// <summary>Removes the standalone client sampler-state ID.</summary>
    public void DeleteSampler(int samplerId) => _standaloneSamplers.Remove(samplerId);

    private static int BytesPerPixel(EnumTextureInternalFormat format) => format switch
    {
        EnumTextureInternalFormat.Rgba8 => 4,
        EnumTextureInternalFormat.Rgba16f => 8,
        EnumTextureInternalFormat.R16f => 2,
        EnumTextureInternalFormat.DepthComponent32 => 4,
        _ => 4,
    };

    // ---------------------------------------------------------------- framebuffers

    /// <summary>Registers a renderer framebuffer with the supplied pixel extent.</summary>
    public int CreateFramebuffer(int width, int height) => _targets.Create((uint)width, (uint)height);

    /// <summary>Associates a renderer texture/layer with the selected framebuffer attachment slot.</summary>
    public void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer)
    {
        int index = attachment == EnumFramebufferAttachment.DepthAttachment
            ? -1
            : (int)attachment - (int)EnumFramebufferAttachment.ColorAttachment0;

        _targets.Attach(framebufferId, index, textureId, (uint)layer);
    }

    /// <summary>Checks retained framebuffer attachment compatibility and reports a client-readable status.</summary>
    public bool CheckFramebufferComplete(int framebufferId, out string status)
    {
        // Dynamic rendering has no framebuffer object to validate, so
        // completeness reduces to having a target with attachments.
        VulkanFramebuffer? framebuffer = _targets.Get(framebufferId);
        if (framebuffer == null)
        {
            status = "no such framebuffer";
            return false;
        }

        status = "complete";
        return true;
    }

    /// <summary>Unbinds and removes a framebuffer through the renderer target manager.</summary>
    public void DeleteFramebuffer(int framebufferId)
    {
        _targets.Delete(framebufferId);
        FramebufferDeleted?.Invoke(framebufferId);
    }

    /// <summary>Host diagnostics; null for a bare device without a game logger.</summary>
    internal VulkanStory.Contracts.IRenderLogger? RenderLogger { get; set; }

    /// <summary>
    /// Raised after a framebuffer is deleted. Its id is reused by the next one created, so a
    /// record keyed on it (the stated draw buffers) has to forget it here.
    /// </summary>
    internal Action<int>? FramebufferDeleted;

    /// <summary>Whether the frame graph records this device's frames. Change only between frames.</summary>
    internal bool FrameGraphEnabled
    {
        get => _graph.Enabled;
        set => _graph.Enabled = value;
    }

    /// <summary>The frame graph's totals. Tests only.</summary>
    internal Graph.FrameGraph FrameGraphForTests => _graph;

    /// <summary>The bound render target's id (0 before any bind).</summary>
    internal int BoundFramebufferId => _targets.Bound?.Id ?? 0;

    /// <summary>The render target standing for the default framebuffer (0 when headless).</summary>
    internal int DefaultFramebufferId => _defaultFramebuffer;

    /// <summary>
    /// Ends the pass a render stage left open (closes its scope): a native draw recorded with
    /// keepScope stays in the stage's declaration until here. No-op with the frame graph off.
    /// </summary>
    internal void EndStagePass()
    {
        if (_frameActive) _targets.EndPass(Commands);
    }

    /// <summary>Lands the clears promoted into a texture before it is written some other way.</summary>
    private void FlushPendingClears(int textureId)
    {
        if (!_frameActive || !_graph.HasPendingClears) return;
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) _targets.FlushPendingClears(Commands, texture);
    }

    /// <summary>
    /// A colour clear of one attachment of an explicit target, outside every native pass: the
    /// promoted LOAD_OP_CLEAR of the next pass on it, or an attachment clear inside an open scope.
    /// The caller has applied the draw buffers and colour mask it stated (VulkanClientPlatform).
    /// </summary>
    internal void ClearNativeColor(int framebufferId, int attachment, float r, float g, float b, float a)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("clearColor attachment=" + attachment + " target=" + _targets.Bound!.Id +
                " rgba=" + r + "," + g + "," + b + "," + a);
        }
        _targets.ClearColor(Commands, attachment, r, g, b, a);
    }

    /// <summary>The depth clear of an explicit target; the caller has applied the stated depth mask.</summary>
    internal void ClearNativeDepth(int framebufferId, float depth)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled) RenderTrace.Write("clearDepth target=" + _targets.Bound!.Id + " depth=" + depth);
        _targets.ClearDepth(Commands, depth);
    }

    private bool BindForNativeClear(int framebufferId)
    {
        EndNativePass();
        if (!_frameActive) return false;
        int id = ResolveNativeFramebuffer(framebufferId);
        VulkanFramebuffer? target = _targets.Get(id);
        if (target == null) return false;
        if (!ReferenceEquals(_targets.Bound, target)) _targets.Bind(Commands, id);
        return true;
    }
}

/// <summary>Texture, sampler, framebuffer and mesh resource operations exposed to the game integration.</summary>
public sealed unsafe partial class VulkanDevice
{
    // --------------------------------------------------------------------- meshes

    private MeshUploads _meshUploads = null!;
    /// <summary>Creates and uploads a mesh through the neutral mesh-data contract.</summary>
    public int CreateMesh(VulkanStory.Contracts.MeshUploadData data, bool staticDraw) =>
        _meshUploads.CreateMesh(data, staticDraw);
    /// <summary>Updates each supplied mesh stream using its declared destination byte offset.</summary>
    public void UpdateMesh(int meshId, VulkanStory.Contracts.MeshUploadData data) =>
        _meshUploads.UpdateMesh(meshId, data);
    /// <summary>Allocates mesh stream capacities and custom layouts without initial data.</summary>
    /// <remarks>All stream size arguments are bytes; the SSBO flag selects packed-face storage and the retained quad-index pattern.</remarks>
    public int CreateEmptyMesh(int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize,
        VulkanStory.Contracts.MeshCustomPartLayout? customFloats, VulkanStory.Contracts.MeshCustomPartLayout? customShorts,
        VulkanStory.Contracts.MeshCustomPartLayout? customBytes, VulkanStory.Contracts.MeshCustomPartLayout? customInts,
        VulkanStory.Contracts.MeshDrawMode drawMode, bool staticDraw, bool ssbo) =>
        _meshUploads.CreateEmptyMesh(xyzSize, normalsSize, uvSize, rgbaSize, flagsSize, indicesSize,
            customFloats, customShorts, customBytes, customInts, drawMode, staticDraw, ssbo);
    /// <summary>Writes packed face-record bytes into a mesh's SSBO-backed position slot.</summary>
    public void UpdateMeshStorageBuffer(int meshId, IntPtr data, int byteOffset, int byteSize) =>
        _meshUploads.UpdateMeshStorageBuffer(meshId, data, byteOffset, byteSize);
    /// <summary>Uploads a previous-particle instance stream owned by this mesh and retired through the frame timeline.</summary>
    internal void UpdateParticleHistory(int meshId, float[] values) => _meshes.UpdateParticleHistory(meshId, values);

    /// <summary>Removes the mesh and schedules its GPU-owned buffers for frame-safe deletion.</summary>
    public void DeleteMesh(int meshId) => _meshes.Delete(meshId, _frames);
}

/// <summary>Which draw command a native draw recorded, so the stats separate the kinds.</summary>
internal enum NativeDrawKind : byte
{
    /// <summary>The three-vertex fullscreen triangle a post pass generates in the shader.</summary>
    Fullscreen = 0,
    /// <summary>One indexed or non-indexed draw of one mesh: sky, entities, GUI quads.</summary>
    Mesh = 1,
    /// <summary>One mesh drawn with per-instance attributes: the particle pools.</summary>
    Instanced = 2,
    /// <summary>One indirect multi-draw out of the per-slot indirect ring: chunk pools and decals.</summary>
    Indirect = 3,
}

/// <summary>
/// Mesh draws on the native device API (docs/vulkan.md, decision 4:
/// "fullscreen triangle, mesh, multi-draw or instanced").
///
/// Stage 1 recorded fullscreen draws only. World systems are mesh draws, so these three entry
/// points join <see cref="VulkanDevice.DrawNativeFullscreen" /> on the same preparation
/// (<c>BeginNativeDraw</c>) and swap the draw command for the mesh manager's:
///
/// - the mesh's own vertex and index buffers, bound by <see cref="MeshManager" />, never a
///   second mesh path of this API's own;
/// - the mesh's interned vertex layout, stated on the pipeline
///   (<see cref="NativePipelineDescription.VertexLayoutId" />) and part of the pipeline key, so
///   a mesh pipeline and a fullscreen pipeline are never the same cache entry;
/// - the real mesh id threaded into <c>BindProgramSets</c>, which is what lets a chunk's
///   storage-buffer vertex fetch and an entity's <c>Animation</c> block resolve per draw
///   instead of against the fullscreen path's hardcoded 0;
/// - multi-draw through the existing per-slot indirect ring (<c>AllocateIndirect</c>), the same
///   regions every multi-draw allocates, so the ring's bookkeeping has one owner.
///
/// What pins it: NativeMeshDrawTests (the ring's use and the pipeline-key dimensions) and
/// NativeSkyTests (the first ported system, old route against native route).
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// The topology a mesh was uploaded with, as the primitive a native pipeline rasterizes it
    /// as. A mesh carries its own <c>EnumDrawMode</c> from the tesselator (triangles for most
    /// geometry, lines for the aiming reticle, a line strip for the camera path), so a native
    /// system states it from the mesh, as GL takes it from the VAO. Triangles for a mesh that
    /// does not exist, so a caller that has already been refused a pipeline sees no surprise.
    /// </summary>
    internal PrimitiveTopology NativeMeshTopology(int meshId) =>
        GlEnums.TopologyFrom(_meshes.Get(meshId)?.DrawMode ?? VulkanStory.Contracts.MeshDrawMode.Triangles);

    /// <summary>Counts one native draw, once in the total and once in its own kind.</summary>
    private void NoteNativeDraw(NativeDrawKind kind)
    {
        VulkanStats.NoteNativeDraw();
        // The generic stated route is counted apart, so the counters below say what the dedicated
        // routes recorded (the differential tests compare the two).
        if (_nativePass is { Generic: true })
        {
            return;
        }
        _nativeDraws++;
        switch (kind)
        {
        case NativeDrawKind.Fullscreen:
            _nativeFullscreenDraws++;
            VulkanStats.NoteNativeFullscreenDraw();
            break;
        case NativeDrawKind.Mesh:
            _nativeMeshDraws++;
            VulkanStats.NoteNativeMeshDraw();
            break;
        case NativeDrawKind.Instanced:
            _nativeInstancedDraws++;
            VulkanStats.NoteNativeInstancedDraw();
            break;
        case NativeDrawKind.Indirect:
            _nativeIndirectDraws++;
            VulkanStats.NoteNativeIndirectDraw();
            break;
        }
    }

    /// <summary>
    /// One indexed draw of one mesh: the sky dome, an entity shape, a GUI quad. The OpenGL body
    /// is <c>ClientPlatformWindows.RenderMesh(MeshRef)</c>.
    /// </summary>
    internal bool DrawNativeMesh(NativePipeline pipeline, int meshId, ReadOnlySpan<NativeTexture> textures) =>
        DrawNativeMeshInstanced(pipeline, meshId, 1, textures);

    /// <summary>
    /// One indexed draw of one mesh with <paramref name="instanceCount" /> instances, the
    /// per-instance attributes coming from the mesh's own instanced bindings (the particle
    /// pools). The OpenGL body is <c>ClientPlatformWindows.RenderMeshInstanced</c>.
    /// </summary>
    internal bool DrawNativeMeshInstanced(NativePipeline pipeline, int meshId, int instanceCount,
        ReadOnlySpan<NativeTexture> textures)
    {
        if (instanceCount <= 0) return false;
        if (!NativeMeshIsDrawable(pipeline, meshId, out VulkanMesh? mesh)) return false;
        if (!BeginNativeDraw(pipeline, textures, meshId, out CommandBuffer commandBuffer, out VulkanFramebuffer? target))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Draw, pipeline.ProgramId, target!.Id, meshId));
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native mesh=" + meshId + " program=" + pipeline.ProgramId + " pass='" +
                _nativePass!.Name + "' target=" + target.Id + " indices=" + mesh!.IndexCount +
                " instances=" + instanceCount);
        }

        _meshes.Bind(commandBuffer, mesh!);
        _context.Api.CmdDrawIndexed(commandBuffer, (uint)mesh!.IndexCount, (uint)instanceCount, 0, 0, 0);
        NoteNativeDraw(instanceCount > 1 ? NativeDrawKind.Instanced : NativeDrawKind.Mesh);
        return true;
    }

    /// <summary>
    /// The multi-draw one mesh pool issues per pass - every surviving range of a chunk pool or
    /// the decal pool in one command - through the existing per-slot indirect ring. The OpenGL
    /// body is <c>ClientPlatformWindows.RenderMesh(MeshRef, int[], int[], int)</c> (glMultiDrawElements).
    ///
    /// <paramref name="indicesStarts" /> holds GL's 64-bit byte offsets as pairs of ints, as
    /// <c>MeshDataPool</c> passes them; <see cref="MeshManager.WriteIndirectCommands" /> is the
    /// one place that converts them.
    /// </summary>
    internal bool DrawNativeMeshMulti(NativePipeline pipeline, int meshId, int[] indicesStarts, int[] indicesSizes,
        int groupCount, ReadOnlySpan<NativeTexture> textures)
    {
        if (groupCount <= 0 || indicesStarts == null || indicesSizes == null) return false;
        if (!NativeMeshIsDrawable(pipeline, meshId, out VulkanMesh? mesh)) return false;
        if (!BeginNativeDraw(pipeline, textures, meshId, out CommandBuffer commandBuffer, out VulkanFramebuffer? target))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.DrawMulti, pipeline.ProgramId, target!.Id, meshId));

        VulkanBuffer indirect = AllocateIndirect(groupCount, out ulong indirectOffset);
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native multidraw mesh=" + meshId + " program=" + pipeline.ProgramId + " pass='" +
                _nativePass!.Name + "' target=" + target.Id + " groups=" + groupCount +
                " indirectOffset=" + indirectOffset);
        }

        _meshes.DrawMulti(commandBuffer, meshId, indicesStarts, indicesSizes, groupCount, indirect, indirectOffset);
        if (_gpuTimestamps != null)
        {
            long indices = 0;
            for (int i = 0; i < groupCount; i++) indices += indicesSizes[i];
            _gpuTimestamps.AddIndices(indices);
        }
        NoteNativeDraw(NativeDrawKind.Indirect);
        return true;
    }

    /// <summary>
    /// Whether the mesh exists, carries what the draw needs, and is the shape the pipeline was
    /// built for. The layout check is the one that matters: a pipeline built for another mesh's
    /// layout would read attributes out of buffers that are not there, which no validation layer
    /// can see because the descriptors are all valid.
    /// </summary>
    private bool NativeMeshIsDrawable(NativePipeline pipeline, int meshId, out VulkanMesh? mesh)
    {
        mesh = _meshes.Get(meshId);
        if (mesh == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native draw skipped: no mesh " + meshId);
            return false;
        }
        if (mesh.Indices == null || mesh.IndexCount == 0)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native draw skipped: mesh " + meshId + " has no indices");
            return false;
        }
        if (mesh.LayoutId != pipeline.Description.VertexLayoutId)
        {
            AddDiagnostic("native draw of mesh " + meshId + " (vertex layout " + mesh.LayoutId +
                ") through a pipeline built for vertex layout " + pipeline.Description.VertexLayoutId);
            return false;
        }
        return true;
    }
}
