using System;
using System.Collections.Generic;

namespace VulkanStory.Render.Vulkan.Core;

/// <summary>
/// How a draw's effective colour write mask reaches the GPU (Phase 2, contract C4).
///
/// The effective mask of attachment i is <c>drawBufferEnabled(i) ? colorMask : 0</c>,
/// then masked by the outputs the program writes. glDrawBuffers and the TAA motion
/// windows change it between draws of one pass; every tier expresses that without
/// restarting the rendering scope. Ordered from most to least capable.
/// </summary>
internal enum ColorWriteTier
{
    /// <summary>
    /// The write-mask set is interned into the pipeline key: a mask change selects
    /// another pipeline. Always available; bounded by programs used inside a window x 2.
    /// </summary>
    PipelineKey = 0,

    /// <summary>
    /// VK_EXT_extended_dynamic_state3 colorWriteMask: the full per-attachment mask is
    /// dynamic, so neither glColorMask nor glDrawBuffers is in the key. With
    /// colorBlendEnable + colorBlendEquation also present the blend set leaves the
    /// key as well.
    /// </summary>
    DynamicMask = 1,

    /// <summary>
    /// VK_EXT_color_write_enable: glDrawBuffers is exactly a per-attachment enable,
    /// zero extra pipelines; glColorMask stays in the key as before.
    /// </summary>
    DynamicEnable = 2,
}

/// <summary>The optional-feature tier table: selection with an env override that forces a fallback.</summary>
internal static class DeviceCaps
{
    /// <summary>enable | mask | pipeline. A forced tier the device lacks degrades to the next one below.</summary>
    public const string ColorWriteTierVariable = "VULKANSTORY_VULKAN_COLOR_WRITE_TIER";

    /// <summary>Parses an override; null for empty or unknown values.</summary>
    public static ColorWriteTier? ParseColorWriteTier(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "enable" or "dynamic-enable" => ColorWriteTier.DynamicEnable,
            "mask" or "dynamic-mask" => ColorWriteTier.DynamicMask,
            "pipeline" or "pipeline-key" or "key" => ColorWriteTier.PipelineKey,
            _ => null,
        };

    /// <summary>
    /// The best tier the device supports, or the forced one when it is supported;
    /// a forced tier the device lacks falls to the best supported tier below it.
    /// </summary>
    public static ColorWriteTier SelectColorWriteTier(bool colorWriteEnable, bool colorWriteMask, ColorWriteTier? forced)
    {
        ColorWriteTier ceiling = forced ?? ColorWriteTier.DynamicEnable;
        if (ceiling == ColorWriteTier.DynamicEnable && colorWriteEnable) return ColorWriteTier.DynamicEnable;
        if (ceiling >= ColorWriteTier.DynamicMask && colorWriteMask) return ColorWriteTier.DynamicMask;
        return ColorWriteTier.PipelineKey;
    }

    /// <summary>The token written to the device log line and the stats.</summary>
    public static string Token(ColorWriteTier tier) => tier switch
    {
        ColorWriteTier.DynamicEnable => "enable",
        ColorWriteTier.DynamicMask => "mask",
        _ => "pipeline",
    };

    /// <summary>Reads the optional color-write capability override from the renderer environment setting.</summary>
    public static ColorWriteTier? FromEnvironment() =>
        ParseColorWriteTier(Environment.GetEnvironmentVariable(ColorWriteTierVariable));
}

/// <summary>
/// What a device reports for the bindless texture model of plan decision 9, read
/// from VkPhysicalDeviceVulkan12Features/Properties and the 1.0 features and limits.
/// </summary>
internal readonly record struct DescriptorIndexingSupport(
    bool RuntimeDescriptorArray,
    bool DescriptorBindingPartiallyBound,
    bool DescriptorBindingSampledImageUpdateAfterBind,
    bool ShaderSampledImageArrayDynamicIndexing,
    uint MaxPerStageDescriptorUpdateAfterBindSampledImages,
    uint MaxPerStageDescriptorUpdateAfterBindSamplers,
    uint MaxDescriptorSetUpdateAfterBindSampledImages,
    uint MaxDescriptorSetUpdateAfterBindSamplers,
    uint MaxDescriptorSetUpdateAfterBindUniformBuffersDynamic,
    uint MaxPushConstantsSize,
    uint MaxPerSetDescriptors);

/// <summary>
/// The device floor for decision 9: one pipeline layout whose set 1 holds
/// combined-image-sampler arrays with PARTIALLY_BOUND | UPDATE_AFTER_BIND, indexed
/// per draw from push constants (docs/vulkan.md#bindless-descriptors, "Limits check at
/// startup" and "Features to enable"). A device below it is not used for Vulkan at
/// all - the session stays on OpenGL - rather than running a second, per-program
/// layout path.
/// </summary>
internal static class DescriptorIndexingFloor
{
    /// <summary>
    /// Sum of the desktop set-1 array capacities (<see cref="Shaders.SetConvention" />, the
    /// starting sizes in docs/vulkan.md#bindless-descriptors "Implementation for this
    /// renderer"). The reduced portability tier declares fewer;
    /// <see cref="RequiredSampledImages" /> follows the selected tier.
    /// </summary>
    public const uint BindlessSampledImages = Shaders.SetConvention.TextureArrayCapacityTotal;

    /// <summary>Set 0's fixed frame textures, with headroom (the plan names five).</summary>
    public const uint FrameTextures = 16;

    /// <summary>
    /// Combined image samplers count against both the sampled-image and the sampler
    /// limits, and the per-stage update-after-bind limits count every set in the
    /// layout, set 0 included.
    /// </summary>
    public static uint RequiredSampledImages(Shaders.SetConvention.TextureArrayCapacities capacities) =>
        capacities.Total + FrameTextures;

    /// <summary>
    /// Set 0's frame block and set 2's program record are both dynamic uniform buffers in
    /// the layout that also holds the update-after-bind set.
    /// </summary>
    public const uint RequiredDynamicUniformBuffers = 2;

    /// <summary>The spec minimum, and the budget decision 9 gives per-draw indices and scalars.</summary>
    public const uint RequiredPushConstantBytes = 128;

    /// <summary>
    /// Every requirement a capacity tier misses, as the feature or limit name the spec
    /// uses (limits with the reported and the required value). Empty when the floor is met.
    /// </summary>
    public static List<string> Missing(in DescriptorIndexingSupport support,
        Shaders.SetConvention.TextureArrayCapacities capacities)
    {
        var missing = new List<string>();
        if (!support.RuntimeDescriptorArray) missing.Add("runtimeDescriptorArray");
        if (!support.DescriptorBindingPartiallyBound) missing.Add("descriptorBindingPartiallyBound");
        if (!support.DescriptorBindingSampledImageUpdateAfterBind) missing.Add("descriptorBindingSampledImageUpdateAfterBind");
        // A per-draw index from a push constant is dynamic but uniform over the draw.
        if (!support.ShaderSampledImageArrayDynamicIndexing) missing.Add("shaderSampledImageArrayDynamicIndexing");

        uint sampledImages = RequiredSampledImages(capacities);
        AtLeast(missing, "maxPerStageDescriptorUpdateAfterBindSampledImages",
            support.MaxPerStageDescriptorUpdateAfterBindSampledImages, sampledImages);
        AtLeast(missing, "maxPerStageDescriptorUpdateAfterBindSamplers",
            support.MaxPerStageDescriptorUpdateAfterBindSamplers, sampledImages);
        AtLeast(missing, "maxDescriptorSetUpdateAfterBindSampledImages",
            support.MaxDescriptorSetUpdateAfterBindSampledImages, sampledImages);
        AtLeast(missing, "maxDescriptorSetUpdateAfterBindSamplers",
            support.MaxDescriptorSetUpdateAfterBindSamplers, sampledImages);
        // Every set of the one layout is judged against the device's per-set descriptor
        // cap; set 1's arrays dominate it (MoltenVK reports 1212 against the desktop
        // set 1's 19456).
        AtLeast(missing, "maxPerSetDescriptors", support.MaxPerSetDescriptors, capacities.Total);
        // Set 0's frame UBO and set 2's program record are dynamic and share the layout
        // with the update-after-bind set.
        AtLeast(missing, "maxDescriptorSetUpdateAfterBindUniformBuffersDynamic",
            support.MaxDescriptorSetUpdateAfterBindUniformBuffersDynamic, RequiredDynamicUniformBuffers);
        AtLeast(missing, "maxPushConstantsSize", support.MaxPushConstantsSize, RequiredPushConstantBytes);
        return missing;
    }

    /// <summary>
    /// Picks the largest capacity tier the device satisfies: the desktop capacities when
    /// it meets them, the reduced portability capacities when only those fit its per-set
    /// descriptor cap, and a refusal otherwise (the desktop tier's misses).
    /// </summary>
    public static bool TrySelectTier(in DescriptorIndexingSupport support,
        out Shaders.SetConvention.TextureArrayCapacities capacities, out List<string> missing)
    {
        capacities = Shaders.SetConvention.DesktopCapacities;
        missing = Missing(support, capacities);
        if (missing.Count == 0) return true;
        List<string> reduced = Missing(support, Shaders.SetConvention.PortabilityCapacities);
        if (reduced.Count != 0) return false;
        capacities = Shaders.SetConvention.PortabilityCapacities;
        missing = [];
        return true;
    }

    private static void AtLeast(List<string> missing, string name, uint reported, uint required)
    {
        if (reported < required) missing.Add($"{name} {reported} < {required}");
    }
}
