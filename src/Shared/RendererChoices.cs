namespace VulkanStory.Settings;

/// <summary>Inclusive bounds of one numeric renderer setting, its nonfinite fallback, and its slider step/scale in the settings panel.</summary>
internal readonly record struct RendererRange(float Min, float Max, float Fallback, int Step, float Scale)
{
    /// <summary>Clamps a finite value into the range; a nonfinite value uses the fallback.</summary>
    internal float Clamp(float value) => float.IsFinite(value) ? Math.Clamp(value, Min, Max) : Fallback;
    /// <summary>Clamps an integer setting into the range.</summary>
    internal int Clamp(int value) => Math.Clamp(value, (int)Min, (int)Max);
    /// <summary>Slider minimum in scaled integer units.</summary>
    internal int SliderMin => (int)MathF.Round(Min * Scale);
    /// <summary>Slider maximum in scaled integer units.</summary>
    internal int SliderMax => (int)MathF.Round(Max * Scale);
}

/// <summary>One source of supported renderer-setting values, numeric ranges and their GUI labels.</summary>
internal static class RendererChoices
{
    private static readonly (string Value, string Label)[] Upscalers = [("off", "Off"), ("dlss", "DLSS"), ("xess", "XeSS"), ("fsr3", "FSR 3.1"), ("fsr4", "FSR 4")];
    private static readonly (string Value, string Label)[] Quality = [("dlaa", "Native AA"), ("quality", "Quality"), ("balanced", "Balanced"), ("performance", "Performance"), ("ultraperformance", "Ultra Performance")];
    private static readonly (string Value, string Label)[] XessQuality = [("dlaa", "Native AA"), ("ultraqualityplus", "Ultra Quality+"), ("ultraquality", "Ultra Quality"), ("quality", "Quality"), ("balanced", "Balanced"), ("performance", "Performance"), ("ultraperformance", "Ultra Performance")];
    private static readonly (string Value, string Label)[] Generation = [("off", "Off"), ("dlss", "DLSS-G"), ("xess", "XeSS-FG"), ("fsr3", "FSR 3 FG")];
    private static readonly (string Value, string Label)[] Latency = [("off", "Off"), ("on", "On"), ("boost", "Boost")];
    private static readonly (string Value, string Label)[] Occlusion = [("auto", "Auto"), ("vanilla", "SSAO"), ("gtao", "GTAO")];
    private static readonly (string Value, string Label)[] OcclusionQuality = [("low", "Low"), ("medium", "Medium"), ("high", "High"), ("ultra", "Ultra")];
    private static readonly (string Value, string Label)[] WorldgenWorkers = [("auto", "Auto"), ("off", "Off"), ("1", "1 worker"), ("2", "2 workers"), ("3", "3 workers")];

    /// <summary>Every provider choice that exists without a vendor SDK.</summary>
    private static readonly (string Value, string Label)[] OffOnly = [("off", "Off")];

    /// <summary>Whether vendor provider choices (upscalers, frame generation, latency) exist on this platform.</summary>
    /// <remarks>Vendor SDKs ship for Windows; DLSS additionally has a Linux runtime. macOS has none, so its
    /// settings surface offers no provider rows at all - the runtime would only fall back.</remarks>
    internal static bool VendorProviders => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

    private static readonly RendererRange RenderScale = new(.25f, 1f, 1f, 5, 100);
    private static readonly RendererRange TaaSharpness = new(0f, 1f, .2f, 5, 100);
    private static readonly RendererRange TaaMipBias = new(-2f, 0f, -.5f, 1, 10);
    private static readonly RendererRange UpscalerLodBiasOffset = new(0f, 1f, 1f, 5, 100);
    private static readonly RendererRange FrameGenerationMultiplier = new(2f, 6f, 2f, 1, 1);
    private static readonly RendererRange EntityShadowDistance = new(16f, 256f, 80f, 8, 1);
    private static readonly RendererRange DroppedItemDistance = new(16f, 256f, 64f, 8, 1);
    private static readonly RendererRange ChiselLodDistance = new(16f, 256f, 48f, 8, 1);
    private static readonly RendererRange ParticleQuantityPercent = new(0f, 100f, 50f, 5, 1);
    private static readonly RendererRange ParticleSpawnLimit = new(0f, 4096f, 256f, 64, 1);
    private static readonly RendererRange ParticlePoolLimit = new(0f, 65536f, 8192f, 1024, 1);

    /// <summary>Returns supported values and labels; XeSS adds its SDK-specific quality choices.</summary>
    /// <param name="key">Renderer setting name shared by commands and the settings panel.</param>
    /// <param name="xess">Whether quality values should include XeSS-specific modes.</param>
    /// <returns>Shared supported values and user-facing labels; callers must not mutate the underlying array.</returns>
    /// <exception cref="ArgumentException">The setting name has no defined choices.</exception>
    internal static IReadOnlyList<(string Value, string Label)> Get(string key, bool xess = false) => key switch
    {
        "Upscaler" => VendorProviders ? Upscalers : OffOnly,
        "UpscalerQuality" => xess ? XessQuality : Quality,
        "FrameGeneration" => VendorProviders ? Generation : OffOnly,
        "LowLatencyMode" => VendorProviders ? Latency : OffOnly,
        "AmbientOcclusion" => Occlusion,
        "AmbientOcclusionPreset" => OcclusionQuality,
        "WorldgenWorkers" => WorldgenWorkers,
        _ => throw new ArgumentException("Unknown renderer choice: " + key),
    };

    /// <summary>Whether a value is one of the supported choices, compared ordinally.</summary>
    /// <param name="key">Renderer setting name accepted by <see cref="Get" />.</param>
    /// <param name="value">Candidate canonical value.</param>
    /// <param name="xess">Whether quality values should include XeSS-specific modes.</param>
    internal static bool Contains(string key, string? value, bool xess = false)
    {
        foreach (var (choice, _) in Get(key, xess))
            if (string.Equals(choice, value, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>Returns the supported numeric range of a renderer setting.</summary>
    /// <param name="key">Renderer setting name shared by normalization, commands and the settings panel.</param>
    /// <exception cref="ArgumentException">The setting name has no defined range.</exception>
    internal static RendererRange Range(string key) => key switch
    {
        "RenderScale" => RenderScale,
        "TaaSharpness" => TaaSharpness,
        "TaaMipBias" => TaaMipBias,
        "UpscalerLodBiasOffset" => UpscalerLodBiasOffset,
        "FrameGenerationMultiplier" => FrameGenerationMultiplier,
        "EntityShadowDistance" => EntityShadowDistance,
        "DroppedItemDistance" => DroppedItemDistance,
        "ChiselLodDistance" => ChiselLodDistance,
        "ParticleQuantityPercent" => ParticleQuantityPercent,
        "ParticleSpawnLimit" => ParticleSpawnLimit,
        "ParticlePoolLimit" => ParticlePoolLimit,
        _ => throw new ArgumentException("Unknown renderer range: " + key),
    };
}
