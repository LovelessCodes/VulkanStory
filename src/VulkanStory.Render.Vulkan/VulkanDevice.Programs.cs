using System;
using System.Collections.Generic;
using VulkanStory.Render.Vulkan.Core;
using VulkanStory.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using VulkanStory.Contracts;
using EnumShaderType = VulkanStory.Render.Vulkan.Shaders.ShaderStageKind;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace VulkanStory.Render.Vulkan;

/// <summary>Shader translation/linking, program lifetime and client uniform writes portion of the renderer.</summary>
public sealed unsafe partial class VulkanDevice
{
    // -------------------------------------------------------------------- shaders

    /// <summary>
    /// Largest shader source accepted per stage. Vanilla's biggest stage is
    /// well under 100 KiB; the cap keeps a broken or hostile mod shader from
    /// handing the native compiler an unbounded input.
    /// </summary>
    internal const int MaxShaderSourceBytes = 2 * 1024 * 1024;

    /// <summary>Stages source for linking without compiling an isolated stage that lacks program-wide interface context.</summary>
    public bool CompileShader(ShaderStageDefinition shader)
    {
        if (shader?.Code == null) return false;

        string stageName = shader.Type.ToString();
        if (shader.Code.Length + (shader.PrefixCode?.Length ?? 0) > MaxShaderSourceBytes)
        {
            AddDiagnostic(VulkanContext.ErrorPrefix + $"{stageName}: shader source exceeds {MaxShaderSourceBytes} bytes and was rejected");
            return false;
        }
        if (shader.Code.IndexOf('\0') >= 0 || (shader.PrefixCode?.IndexOf('\0') ?? -1) >= 0)
        {
            AddDiagnostic(VulkanContext.ErrorPrefix + $"{stageName}: shader source contains a NUL byte and was rejected");
            return false;
        }

        _stagedStages.AddOrUpdate(shader, new StagedStage
        {
            Stage = (ShaderStageKind)shader.Type,
            Code = shader.Code,
            PrefixCode = shader.PrefixCode ?? "",
            Filename = shader.Type.ToString(),
        });
        return true;
    }

    /// <summary>Builds the program interface and links native artifacts or translated client shader stages.</summary>
    /// <returns>Registered program ID, or the retained failure sentinel when compilation/linking fails.</returns>
    public int LinkProgram(ShaderProgramDefinition program)
    {
        var stages = new List<ShaderStageSource>();
        AddStage(stages, program.VertexShader, EnumShaderType.VertexShader, program.PassName);
        AddStage(stages, program.FragmentShader, EnumShaderType.FragmentShader, program.PassName);
        AddStage(stages, program.GeometryShader, EnumShaderType.GeometryShader, program.PassName);

        if (stages.Count == 0)
        {
            AddDiagnostic(VulkanContext.ErrorPrefix + $"shader program '{program.PassName}' has no stages");
            return 0;
        }

        // The seam (docs/vulkan.md): a program the manifest has links from its
        // SPIR-V; one it has not links through the rewriter; one it has but cannot serve (a bad hash, an
        // unreadable define, a module the driver refuses) links through the rewriter and counts as failed.
        string passName = program.PassName ?? "";
        ShaderProgramResources? resources = null;
        TranslatedProgram? native = null;
        bool nativeFailed = false;
        string nativeDetail = "";
        if (_nativeShaders != null && _modShaderScan != null && _modShaderScan(passName))
        {
            // A mod replaced this program's GLSL (or the scan cannot rule it out): the native SPIR-V would draw
            // vanilla over the mod, so the mod's source goes through the rewriter. Rewritten, not failed.
            ReportModOverride(passName);
        }
        else if (_nativeShaders != null)
        {
            // A selected pack's program outranks the shipped one (docs/shader-packs.md): a slot the
            // pack did not build, or could not serve, falls back to the shipped program.
            NativeShaderLibrary.Outcome outcome = NativeShaderLibrary.Outcome.Miss;
            if (_shaderPack != null)
            {
                outcome = _shaderPack.Library.TryLink(passName, stages, out native, out nativeDetail);
                if (outcome == NativeShaderLibrary.Outcome.Native)
                {
                    if (RenderTrace.Enabled)
                    {
                        RenderTrace.Write("program '" + passName + "' linked from pack '" + _shaderPack.Manifest.Id + "' [" + nativeDetail + "]");
                    }
                }
                else if (outcome == NativeShaderLibrary.Outcome.Failed)
                {
                    ReportPackFailure(passName, nativeDetail);
                }
            }
            if (outcome != NativeShaderLibrary.Outcome.Native)
            {
                outcome = _nativeShaders.TryLink(passName, stages, out native, out nativeDetail);
                nativeFailed = outcome == NativeShaderLibrary.Outcome.Failed;
                if (outcome != NativeShaderLibrary.Outcome.Native) native = null;
            }
        }

        int programId = 0;
        if (native != null)
        {
            programId = _nextProgramId++;
            try
            {
                var limits = _context.Api.GetPhysicalDeviceProperties(_context.PhysicalDevice).Limits;
                int output = InterfaceComponents(SpirvReflection.Reflect(native.Spirv[EnumShaderType.VertexShader]).Outputs);
                int input = InterfaceComponents(SpirvReflection.Reflect(native.Spirv[EnumShaderType.FragmentShader]).Inputs);
                if (output > limits.MaxVertexOutputComponents || input > limits.MaxFragmentInputComponents)
                    throw new InvalidOperationException("Native shader interface exceeds this device's vertex-output/fragment-input limits.");
                resources = new ShaderProgramResources(_context, programId, native, _sharedLayout!.Layout);
            }
            catch (InvalidOperationException error)
            {
                nativeFailed = true;
                nativeDetail += ": " + error.Message;
            }
        }
        if (nativeFailed) ReportNativeFailure(passName, nativeDetail);

        TranslatedProgram translated;
        if (resources != null)
        {
            translated = native!;
            _nativeLinks++;
        }
        else
        {
            if (nativeFailed) _failedNativeLinks++;
            else _rewrittenLinks++;

            // The include files the registry assembled the program from decide which
            // of its uniforms read the shared frame block. A program built any other
            // way - a mod's, a test's - keeps every uniform to itself.
            translated = ShaderTranslator.Translate(stages, _shaderCompiler, null,
                program.Includes);
            if (!translated.Success)
            {
                foreach (string error in translated.Errors)
                {
                    AddDiagnostic(VulkanContext.ErrorPrefix + $"{program.PassName}: {error}");
                }
                return 0;
            }

            if (programId == 0) programId = _nextProgramId++;
        }
        if (RenderTrace.Enabled)
        {
            RenderTrace.DumpProgramSources(passName, translated);
            RenderTrace.Write("program " + programId + " '" + program.PassName + "' uniformBlockBytes=" +
                translated.Layout.BlockSize + " pushBytes=" + translated.Layout.PushConstantSize +
                (translated.IsNative ? " native [" + nativeDetail + "]" : ""));
            foreach (UniformMember member in translated.Layout.Members)
            {
                RenderTrace.Write("  uniform " + member.Name + " offset=" + member.Offset +
                    " type=" + member.Type + " count=" + member.ArrayLength);
            }
        }
        resources ??= new ShaderProgramResources(_context, programId, translated, _sharedLayout!.Layout);
        _programs[programId] = resources;
        // The variant a native program was linked for (TryLink reports the key as its detail),
        // so a native pipeline request can state the variant it expects.
        if (resources.IsNative) _programVariants[programId] = nativeDetail;
        _programNames[programId] = program.PassName ?? "";
        // Pipelines an earlier launch used with this exact program start compiling now.
        int prewarming = _pipelines.PrewarmFor(resources);
        if (prewarming > 0 && RenderTrace.Enabled)
        {
            RenderTrace.Write("program " + programId + " prewarming " + prewarming + " pipelines");
        }
        return programId;
    }

    /// <summary>
    /// Loads the native shaders once, at device start: the directory or pack a test named, the source tree
    /// <c>VULKANSTORY_VK_SHADER_SOURCE</c> names, or <c>shaders-vk.pak</c> (else a <c>shaders-vk</c> directory) in
    /// <see cref="NativeShaderPackageDirectory" /> or beside the renderer assembly. One log line says what came of it.
    /// </summary>
    private void LoadNativeShaders()
    {
        string? searchDirectory = NativeShaderPackageDirectory;
        if (string.IsNullOrEmpty(searchDirectory))
        {
            try
            {
                searchDirectory = System.IO.Path.GetDirectoryName(typeof(VulkanDevice).Assembly.Location);
            }
            catch (Exception error) when (error is ArgumentException or System.IO.PathTooLongException)
            {
                // An assembly loaded from bytes has no location; the resolution below reports it.
            }
        }

        (NativeShaderLibrary.Mode mode, string? path, string reason) = NativeShaderLibrary.Resolve(
            NativeShadersEnabled, NativeShaderDirectory,
            Environment.GetEnvironmentVariable(NativeShaderLibrary.EnabledVariable),
            Environment.GetEnvironmentVariable(NativeShaderLibrary.SourceVariable),
            searchDirectory);

        _nativeShaders = mode switch
        {
            NativeShaderLibrary.Mode.Directory => NativeShaderLibrary.Load(path!, _shaderCompiler.Identity, out reason),
            NativeShaderLibrary.Mode.Pack => NativeShaderLibrary.LoadPack(path!, _shaderCompiler.Identity, out reason),
            NativeShaderLibrary.Mode.Source => NativeShaderLibrary.BuildFromSource(path!, _shaderCompiler, out reason),
            _ => null,
        };

        string enabledVariable = Environment.GetEnvironmentVariable(NativeShaderLibrary.EnabledVariable) ?? "";
        bool ignoreScan = IgnoreModShaderScan ?? NativeShaderLibrary.IgnoresModScan(enabledVariable);
        _modShaderScan = ignoreScan ? null : ShaderProgramOverriddenByMods;

        LoadShaderPack(searchDirectory);

        NativeShaderStatus = _nativeShaders == null
            ? "off: " + reason
            : _nativeShaders.Manifest.Programs.Count + " programs from " + _nativeShaders.Origin +
              (reason.Length > 0 ? "; " + reason : "");
        if (_shaderPackStatus.Length > 0) NativeShaderStatus += "; " + _shaderPackStatus;
        if (_nativeShaders != null && ignoreScan && ShaderProgramOverriddenByMods != null)
        {
            NativeShaderStatus += "; mod shader scan ignored (" + NativeShaderLibrary.EnabledVariable + "=" + NativeShaderLibrary.ForceValue + ")";
        }
        else if (_nativeShaders != null && _modShaderScan != null && _modShaderScan(AllShaderProgramsEntry))
        {
            NativeShaderStatus += "; the mod shader scan makes every program rewriter-only (no report, a failed scan, or a shaderincludes override)";
        }
        LogShaderLine("[VulkanStory] shaders: native " + NativeShaderStatus);
    }

    /// <summary>The scan's entry for every program (<c>OptimumConfig.AllShaderPrograms</c>, the scanner's <c>AllPrograms</c>).</summary>
    internal const string AllShaderProgramsEntry = "all";

    /// <summary>Logs, once per program the manifest has, that the mod shader scan sent it to the rewriter.</summary>
    private void ReportModOverride(string passName)
    {
        if (_nativeShaders?.Manifest.FindProgram(passName) == null) return;
        bool all = _modShaderScan!(AllShaderProgramsEntry);
        lock (_modOverrideLogged)
        {
            if (!_modOverrideLogged.Add(passName)) return;
        }
        string line = "[VulkanStory] shaders: native '" + passName + "' linked through the rewriter: " +
            (all ? "the mod shader scan makes every program rewriter-only" : "a mod replaces its GLSL (launcher shader scan)");
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    private void ReportNativeFailure(string passName, string detail)
    {
        string line = "[VulkanStory] shaders: native '" + passName + "' failed, linked through the rewriter: " + detail;
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    /// <summary>
    /// Loads the selected shader pack over the shipped library (docs/shader-packs.md). Runs from
    /// <see cref="LoadNativeShaders" />, after the shipped manifest is known and before any program links,
    /// so every slot can be validated against it. The selection is <see cref="NativeShaderLibrary.PackVariable" />
    /// until the Options menu carries it.
    /// </summary>
    private void LoadShaderPack(string? searchDirectory)
    {
        string selection = Environment.GetEnvironmentVariable(NativeShaderLibrary.PackVariable)?.Trim() ?? "";
        if (selection.Length == 0) return;

        if (_nativeShaders == null)
        {
            _shaderPackStatus = "shader pack '" + selection + "' not loaded: native shaders are off";
            return;
        }

        string? packDirectory = ResolvePackDirectory(selection, searchDirectory);
        if (packDirectory == null)
        {
            _shaderPackStatus = "shader pack '" + selection + "' not loaded: no directory at " +
                (System.IO.Path.IsPathRooted(selection)
                    ? selection
                    : System.IO.Path.Combine(searchDirectory ?? "", "shaderpacks", selection));
            return;
        }

        string? cacheRoot = ResolveShaderCacheRoot(ShaderCacheDirectory,
            Environment.GetEnvironmentVariable("VULKANSTORY_VULKAN_SHADER_CACHE"));
        if (cacheRoot == null)
        {
            _shaderPackStatus = "shader pack '" + selection + "' not loaded: no shader cache directory";
            return;
        }

        ShaderPackLibrary? pack = ShaderPackLibrary.Load(packDirectory, _nativeShaders.Manifest, _shaderCompiler, cacheRoot, out string reason);
        if (pack == null)
        {
            _shaderPackStatus = "shader pack '" + selection + "' not loaded: " + reason;
            return;
        }

        _shaderPack = pack;
        foreach (KeyValuePair<string, string> failure in pack.Failures)
        {
            LogShaderLine("[VulkanStory] shaders: pack '" + pack.Manifest.Id + "' program '" + failure.Key +
                "' did not build, the shipped program is used: " + failure.Value);
        }
        _shaderPackStatus = "pack '" + pack.Manifest.Id + "' replaces " + pack.BuiltPrograms + " of " +
            pack.Manifest.Programs.Count + " declared program(s)" +
            (pack.Failures.Count > 0 ? ", " + pack.Failures.Count + " refused" : "");
        LogShaderLine("[VulkanStory] shaders: pack '" + pack.Manifest.Id + "' built from " + pack.Origin);
    }

    /// <summary>An absolute pack directory, or an id under <c>shaderpacks/</c> beside the runtime.</summary>
    private static string? ResolvePackDirectory(string selection, string? searchDirectory)
    {
        if (System.IO.Path.IsPathRooted(selection))
        {
            return System.IO.Directory.Exists(selection) ? selection : null;
        }
        if (string.IsNullOrEmpty(searchDirectory)) return null;
        string candidate = System.IO.Path.Combine(searchDirectory, "shaderpacks", selection);
        return System.IO.Directory.Exists(candidate) ? candidate : null;
    }

    /// <summary>Logs, once per program, that a pack's program could not serve a link and the shipped program is used.</summary>
    private void ReportPackFailure(string passName, string detail)
    {
        lock (_packFailureLogged)
        {
            if (!_packFailureLogged.Add(passName)) return;
        }
        string line = "[VulkanStory] shaders: pack '" + (_shaderPack?.Manifest.Id ?? "?") + "' program '" + passName +
            "' failed to link, the shipped program is used: " + detail;
        LogShaderLine(line);
        if (RenderTrace.Enabled) RenderTrace.Write(line);
    }

    /// <summary>
    /// The line after a shader load: the programs linked since the last report. ShaderRegistry links every program
    /// of a load or reload in one synchronous call, so the first frame after links is the end of that load.
    /// </summary>
    private void ReportShaderLoad()
    {
        int native = _nativeLinks - _nativeLinksReported;
        int rewritten = _rewrittenLinks - _rewrittenLinksReported;
        int failed = _failedNativeLinks - _failedNativeLinksReported;
        if (native + rewritten + failed == 0) return;

        _nativeLinksReported = _nativeLinks;
        _rewrittenLinksReported = _rewrittenLinks;
        _failedNativeLinksReported = _failedNativeLinks;
        LogShaderLine("[VulkanStory] shaders: " + native + " native, " + rewritten + " rewritten, " + failed + " failed");
        VulkanStats.NoteShaderLoad(native, rewritten, failed);
    }

    private static void LogShaderLine(string line)
    {
        Console.WriteLine(line);
        MirrorValidationMessage("--- " + line);
    }

    private void AddStage(List<ShaderStageSource> stages, ShaderStageDefinition? shader, EnumShaderType stage, string passName)
    {
        if (shader == null || !_stagedStages.TryGetValue(shader, out StagedStage? staged)) return;

        stages.Add(new ShaderStageSource
        {
            Stage = stage,
            Code = staged.Code,
            PrefixCode = staged.PrefixCode,
            Filename = passName + StageExtension(stage),
        });
    }

    private static string StageExtension(EnumShaderType stage) => stage switch
    {
        EnumShaderType.VertexShader => ".vsh",
        EnumShaderType.FragmentShader => ".fsh",
        _ => ".gsh",
    };

    /// <summary>Removes a program and schedules its GPU resources for safe frame retirement.</summary>
    /// <summary>Counts the highest reflected interface location, including array and matrix spans.</summary>
    private static int InterfaceComponents(IEnumerable<SpirvInterfaceVariable> variables)
    {
        int extent = 0;
        foreach (var variable in variables)
        {
            int columns = variable.GlslType.StartsWith("mat", StringComparison.Ordinal) &&
                variable.GlslType.Length > 3 && char.IsDigit(variable.GlslType[3]) ? variable.GlslType[3] - '0' : 1;
            extent = Math.Max(extent, (variable.Location + columns * Math.Max(1, variable.ArrayLength)) * 4);
        }
        return extent;
    }

    public void DeleteProgram(int programId)
    {
        if (!_programs.Remove(programId, out ShaderProgramResources? program)) return;
        _programNames.Remove(programId);
        // No background compile may still be reading its modules or layout.
        _pipelines.CancelProgram(program);
        if (_pipelines.RemoveProgramPipelines(programId) is { } pipelines) _frames.DeferDeletion(pipelines);
        ForgetNativePipelines(programId);
        _frames.DeferDeletion(program);
    }

    /// <summary>Returns the program's retained named-uniform location, or the missing-location sentinel.</summary>
    public int GetUniformLocation(int programId, string name) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? program.LocationOf(name) : -1;

    // ------------------------------------------------------------------- uniforms

    private void Write(int programId, int location, ReadOnlySpan<byte> data)
    {
        // A member of the shared frame block: one shadow for every program.
        if (ShaderProgramResources.IsFrameLocation(location))
        {
            WriteFrameGlobal(location - ShaderProgramResources.FrameLocationBase, data);
            return;
        }

        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            // A native program's push member (a DRAW uniform): kept per program, pushed per draw.
            if (ShaderProgramResources.IsPushLocation(location)) program.SetPushUniform(location, data);
            else program.SetUniform(location, data);
        }
    }

    /// <summary>
    /// Writes into the shared frame block. Use() rewrites the same values on every
    /// program switch, so the common case is bytes that already match: a comparison
    /// and nothing else. A real change bumps the version and the next draw takes a
    /// new snapshot.
    /// </summary>
    private void WriteFrameGlobal(int offset, ReadOnlySpan<byte> data)
    {
        if (offset < 0 || offset + data.Length > _frameGlobals.Length) return;

        Span<byte> destination = _frameGlobals.AsSpan(offset, data.Length);
        if (data.SequenceEqual(destination)) return;

        data.CopyTo(destination);
        _frameGlobalsVersion++;
    }

    /// <summary>A copy of a linked program's record shadow, initializers included; null for an unknown program. For tests.</summary>
    internal byte[]? ProgramRecordForTests(int programId) =>
        _programs.TryGetValue(programId, out ShaderProgramResources? program) ? (byte[])program.UniformShadow.Clone() : null;

    /// <summary>A copy of the shared frame block's current bytes. For tests.</summary>

    public void SetUniform(int programId, int location, float value) =>
        Write(programId, location, new ReadOnlySpan<byte>(&value, sizeof(float)));

    /// <summary>Assigns a sampler's texture unit or writes an integer into the linked program's CPU uniform shadow.</summary>
    /// <remarks>Sampler locations update the descriptor unit table; other unresolved locations are ignored.</remarks>
    public void SetUniform(int programId, int location, int value)
    {
        // Assigning a sampler its texture unit is an int write to its uniform
        // location in GL. Here the sampler is a descriptor binding, so the same
        // call has to reach the unit table instead of the uniform block.
        if (ShaderProgramResources.IsSamplerLocation(location))
        {
            if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
            {
                program.SetSamplerUnitByLocation(location, value);
            }
            return;
        }
        Write(programId, location, new ReadOnlySpan<byte>(&value, sizeof(int)));
    }

    /// <summary>Writes a scalar/vector value into the linked program's CPU uniform shadow.</summary>
    /// <remarks>A negative or unresolved location is ignored by the retained uniform-writing path.</remarks>
    public void SetUniform(int programId, int location, int x, int y, int z)
    {
        // Scalar block layout stores an ivec3 as three consecutive 32-bit ints.
        int* values = stackalloc int[3] { x, y, z };
        Write(programId, location, new ReadOnlySpan<byte>(values, 3 * sizeof(int)));
    }

    /// <summary>Writes a scalar/vector value into the linked program's CPU uniform shadow.</summary>
    /// <remarks>A negative or unresolved location is ignored by the retained uniform-writing path.</remarks>
    public void SetUniform(int programId, int location, float x, float y)
    {
        float* values = stackalloc float[2] { x, y };
        Write(programId, location, new ReadOnlySpan<byte>(values, 2 * sizeof(float)));
    }

    /// <summary>Writes a scalar/vector value into the linked program's CPU uniform shadow.</summary>
    /// <remarks>A negative or unresolved location is ignored by the retained uniform-writing path.</remarks>
    public void SetUniform(int programId, int location, float x, float y, float z)
    {
        float* values = stackalloc float[3] { x, y, z };
        Write(programId, location, new ReadOnlySpan<byte>(values, 3 * sizeof(float)));
    }

    /// <summary>Writes a scalar/vector value into the linked program's CPU uniform shadow.</summary>
    /// <remarks>A negative or unresolved location is ignored by the retained uniform-writing path.</remarks>
    public void SetUniform(int programId, int location, float x, float y, float z, float w)
    {
        float* values = stackalloc float[4] { x, y, z, w };
        Write(programId, location, new ReadOnlySpan<byte>(values, 4 * sizeof(float)));
    }

    // The array setters are a straight memcpy because the generated block uses
    // scalar layout, where a float[] packs exactly as the shader expects. Under
    // std140 each of these would need re-striding on the way in.
    private void WriteArray(int programId, int location, int count, float[] values, int componentsPerElement)
    {
        int floats = Math.Min(values.Length, count * componentsPerElement);
        if (floats <= 0) return;

        fixed (float* source = values)
        {
            Write(programId, location, new ReadOnlySpan<byte>(source, floats * sizeof(float)));
        }
    }

    /// <summary>Writes 1-component float array elements to the program's CPU uniform shadow.</summary>
    /// <param name="programId">Linked renderer program ID.</param>
    /// <param name="location">Resolved uniform location.</param>
    /// <param name="count">Number of array elements.</param>
    /// <param name="values">Borrowed component values; writes are limited to the available values and requested element count.</param>
    public void SetUniformArray1(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 1);

    /// <summary>Writes 2-component float array elements to the program's CPU uniform shadow.</summary>
    /// <param name="programId">Linked renderer program ID.</param>
    /// <param name="location">Resolved uniform location.</param>
    /// <param name="count">Number of array elements.</param>
    /// <param name="values">Borrowed component values; writes are limited to the available values and requested element count.</param>
    public void SetUniformArray2(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 2);

    /// <summary>Writes 3-component float array elements to the program's CPU uniform shadow.</summary>
    /// <param name="programId">Linked renderer program ID.</param>
    /// <param name="location">Resolved uniform location.</param>
    /// <param name="count">Number of array elements.</param>
    /// <param name="values">Borrowed component values; writes are limited to the available values and requested element count.</param>
    public void SetUniformArray3(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 3);

    /// <summary>Writes 4-component float array elements to the program's CPU uniform shadow.</summary>
    /// <param name="programId">Linked renderer program ID.</param>
    /// <param name="location">Resolved uniform location.</param>
    /// <param name="count">Number of array elements.</param>
    /// <param name="values">Borrowed component values; writes are limited to the available values and requested element count.</param>
    public void SetUniformArray4(int programId, int location, int count, float[] values) =>
        WriteArray(programId, location, count, values, 4);

    /// <summary>Writes one 4x4 float matrix into the program uniform shadow.</summary>
    public void SetUniformMatrix(int programId, int location, float[] matrix) =>
        WriteArray(programId, location, 1, matrix, 16);

    /// <summary>Writes an array of 4x4 float matrices into the program uniform shadow.</summary>
    public void SetUniformMatrices(int programId, int location, int count, float[] matrices) =>
        WriteArray(programId, location, count, matrices, 16);

    /// <summary>Copies client 4x3 matrices as twelve floats per element into the scalar-layout uniform shadow.</summary>
    public void SetUniformMatrices4x3(int programId, int location, int count, float[] matrices) =>
        WriteArray(programId, location, count, matrices, 12);

    /// <summary>
    /// The samplers a linked program declares, in declaration order.
    ///
    /// Not part of the seam - the client never needs it, because it binds the
    /// samplers it knows by name. It exists so a test can bind every sampler a
    /// real program declares without hardcoding the list, since a draw whose
    /// descriptor set is incomplete is skipped rather than drawn.
    /// </summary>
    internal string[] SamplerNamesOf(int programId)
    {
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            return program.SamplerNames;
        }
        return Array.Empty<string>();
    }

    /// <summary>Associates a named sampler with a client texture unit in the linked program interface.</summary>
    public void SetSamplerUnit(int programId, string samplerName, int unit)
    {
        if (_programs.TryGetValue(programId, out ShaderProgramResources? program))
        {
            program.SetSamplerUnitByName(samplerName, unit);
        }
    }

    // Uniform state is game-neutral; draw binding consumes its retained dictionaries.
    private readonly ClientUniformBufferManager _clientUniformBuffers = new();
    /// <summary>Looks up a named client block's descriptor binding in the linked program.</summary>
    internal int? ClientUniformBlockBinding(int programId, string blockName)
    {
        // Per entity per pass while motion is on (UBO.Update("Animation")): a plain loop, no LINQ closure.
        if (!_programs.TryGetValue(programId, out ShaderProgramResources? program)) return null;
        foreach (BlockBinding block in program.Interface.UniformBlocks)
        {
            if (block.BlockName == blockName) return block.Binding;
        }
        return null;
    }

    /// <summary>Creates and binds a CPU-shadowed named client uniform buffer.</summary>
    public int CreateUniformBuffer(int programId, int bindingPoint, string blockName, int size) =>
        _clientUniformBuffers.CreateUniformBuffer(programId, bindingPoint, blockName, size);
    /// <summary>Updates a valid byte range in the named client uniform-buffer shadow.</summary>
    public void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size) =>
        _clientUniformBuffers.UpdateUniformBuffer(handle, data, offset, size);
    /// <summary>Associates the buffer with its named block for subsequent draw snapshots.</summary>
    public void BindUniformBuffer(int handle) => _clientUniformBuffers.BindUniformBuffer(handle);
    /// <summary>Preserves the indexed named-block binding when the client clears its generic UBO target.</summary>
    public void UnbindUniformBuffer(int handle) => _clientUniformBuffers.UnbindUniformBuffer(handle);
    /// <summary>Removes the client uniform-buffer shadow and its current named-block association.</summary>
    public void DeleteUniformBuffer(int handle) => _clientUniformBuffers.DeleteUniformBuffer(handle);
    // Observation for changed game/renderer boundary fixtures; never exposes mutable storage.
    internal byte[]? UniformBufferShadowForTests(int handle) =>
        _clientUniformBuffers.Buffers.TryGetValue(handle, out var buffer) ? (byte[])buffer.Shadow.Clone() : null;
}
