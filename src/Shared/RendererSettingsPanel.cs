using System.Text.Json;
using System.Text.Json.Nodes;
using Vintagestory.API.Client;

namespace VulkanStory.Settings;

// Shared source linked into the early menu integration and ordinary API-only mod.
/// <summary>Shared renderer settings draft, live preview, Save/Cancel behavior, and page composition for both GUI hosts.</summary>
/// <remarks>Runtime actions are supplied through process callbacks; failed actions remain visible on the current page.</remarks>
internal sealed class RendererSettingsPanel
{
    private readonly JsonObject draft;
    private readonly Func<string, string?> save;
    private readonly Func<string, string?> preview;
    private string committed;
    private bool previewApplied;
    private readonly Action<string> notify;
    private readonly Action close;
    private readonly Func<bool>? editingActive;
    private readonly Func<string?> controllerOpened;
    private bool controllerPending;
    private int page;
    internal static readonly string[] PageNames = ["Image", "Generation", "Effects", "Device", "Performance", "Status"];
    /// <summary>Display name of the currently selected settings page.</summary>
    internal string CurrentPageName => PageNames[page];
    private bool refresh;
    private string? errorMessage;
    /// <summary>Revision incremented whenever a nonnull error is displayed, allowing hosts to reveal it.</summary>
    internal long ErrorRevision { get; private set; }
    /// <summary>Current error block's composed Y position, or null when no error block exists.</summary>
    internal double? ErrorContentY { get; private set; }
    private long nextStatusRefresh;
    private Action? refreshImageStatus;
    /// <summary>Creates an editable JSON draft and retains the initial committed state for Cancel restoration.</summary>
    /// <param name="json">Current renderer settings serialized as a JSON object.</param>
    /// <param name="apply">Persists/applies a draft, returning null on success or a visible error message.</param>
    /// <param name="notify">Receives success notifications.</param>
    /// <param name="close">Closes the current settings host after successful Save/Cancel.</param>
    /// <param name="editingActive">Optional guard used by deferred controller-open callbacks.</param>
    /// <param name="controllerOpened">Optional host transition after controller settings open; null uses the close callback.</param>
    internal RendererSettingsPanel(string json, Func<string, string?> apply, Action<string> notify, Action close,
        Func<bool>? editingActive = null, Func<string?>? controllerOpened = null)
    {
        draft = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Renderer settings are unavailable.");
        committed = json;
        preview = AppContext.GetData("VulkanStory.Runtime.PreviewSettings") as Func<string, string?>
            ?? (_ => "Live renderer settings are unavailable.");
        save = apply; this.notify = notify; this.close = close;
        this.editingActive = editingActive; this.controllerOpened = controllerOpened ?? (() => { close(); return null; });
    }
    /// <summary>Consumes the refresh flag and updates visible status at most once per second.</summary>
    /// <returns>Whether the host should recompose its content; Image status text may update in place.</returns>
    internal bool TakeRefresh()
    {
        bool value = refresh; refresh = false;
        if (Environment.TickCount64 >= nextStatusRefresh)
        {
            nextStatusRefresh = Environment.TickCount64 + 1000;
            if (page == 0) refreshImageStatus?.Invoke();
            if (PageNames[page] == "Status") return true;
        }
        return value;
    }
    private bool Save()
    {
        string? error = save(draft.ToJsonString());
        if (error != null) { ShowError("Could not save: " + error); return true; }
        committed = draft.ToJsonString(); previewApplied = false;
        notify("VulkanStory settings saved. Options marked restart apply after restarting the game.");
        close(); return true;
    }
    private bool Cancel()
    {
        string? error = EndPreview();
        if (error != null) { ShowError("Could not restore settings: " + error); return true; }
        close(); return true;
    }
    /// <summary>Restores the last committed JSON state when a live preview is active.</summary>
    /// <returns>Null when no restoration is needed or restoration succeeded; otherwise the runtime callback's error.</returns>
    internal string? EndPreview()
    {
        if (!previewApplied) return null;
        string? error = preview(committed);
        if (error == null) previewApplied = false;
        return error;
    }
    private void Preview()
    {
        string? error = preview(draft.ToJsonString());
        if (error == null) previewApplied = true;
        if (error != null || errorMessage != null)
            ShowError(error == null ? null : "Could not apply settings: " + error);
    }
    private void ShowError(string? error)
    {
        errorMessage = error;
        if (error != null) ErrorRevision++;
        refresh = true;
    }
    private bool SaveAndOpenController()
    {
        if (controllerPending) return true;
        if (AppContext.GetData("VulkanStory.Runtime.ControllerSettingsAfterSave") is not
            Func<string, Func<bool>, Action<string?>, string?> request)
        { ShowError("Controller settings are unavailable."); return true; }
        var weak = new WeakReference<RendererSettingsPanel>(this);
        controllerPending = true;
        string? error = request(draft.ToJsonString(),
            () => weak.TryGetTarget(out var panel) && panel.controllerPending && panel.editingActive?.Invoke() != false,
            result =>
            {
                if (!weak.TryGetTarget(out var panel) || !panel.controllerPending || panel.editingActive?.Invoke() == false) return;
                panel.controllerPending = false;
                panel.ShowError(result ?? panel.controllerOpened());
            });
        if (error != null) { controllerPending = false; ShowError(error); }
        else { committed = draft.ToJsonString(); previewApplied = false; }
        refresh = true;
        return true;
    }
    /// <summary>Adds standalone settings content, composes the host, and initializes widgets after composition.</summary>
    internal GuiComposer Compose(GuiComposer composer, Func<bool>? canInteract = null)
    {
        Action initialize = AddContent(composer, 550, out _, embedded: false, canInteract: canInteract);
        GuiComposer result = composer.EndChildElements().Compose();
        initialize();
        return result;
    }
    /// <summary>Adds width-adaptive page buttons and returns the height reserved for navigation.</summary>
    internal double AddPageNavigation(GuiComposer composer, double width, double y = 0,
        Func<bool>? canInteract = null)
    {
        string[] pages = PageNames;
        int columns = Math.Max(1, Math.Min(pages.Length, (int)(width / 110)));
        for (int index = 0; index < pages.Length; index++)
        {
            int selected = index;
            composer.AddSmallButton(pages[index], () =>
                { if (canInteract?.Invoke() != false && !controllerPending) { page = selected; refresh = true; } return true; },
                ElementBounds.Fixed(index % columns * 110, y + index / columns * 36, 100, 28),
                key: "vulkanstory-page-" + pages[index]);
        }
        return Math.Ceiling((double)pages.Length / columns) * 36 + 12;
    }

    /// <summary>Adds a single-row page selector for standalone hosts with a bounded viewport.</summary>
    internal void AddCompactPageNavigation(GuiComposer composer, double width, Func<bool>? canInteract = null)
    {
        composer.AddDropDown(PageNames, PageNames, page, (value, selected) =>
        {
            if (!selected || canInteract?.Invoke() == false || controllerPending) return;
            int index = Array.IndexOf(PageNames, value);
            if (index < 0) return;
            page = index; refresh = true;
        }, ElementBounds.Fixed(0, 0, width, 28), "vulkanstory-pages");
    }
    /// <summary>Adds the selected page to a composer under construction and reports its content extent.</summary>
    /// <param name="composer">Host composer to populate.</param>
    /// <param name="width">Available content width in GUI layout units.</param>
    /// <param name="height">Resulting content height for the host's layout or scroll viewport.</param>
    /// <param name="embedded">Whether to use the embedded Options spacing and adaptive text layout.</param>
    /// <param name="canInteract">Optional current-host guard checked by user callbacks.</param>
    /// <param name="includeFooter">Whether to append Save/Cancel controls.</param>
    /// <param name="includePages">Whether to append page navigation before the content.</param>
    /// <returns>Widget initialization callback to invoke after the host composer has been composed.</returns>
    internal Action AddContent(GuiComposer composer, double width, out double height, bool embedded = true,
        Func<bool>? canInteract = null, bool includeFooter = true, bool includePages = true)
    {
        bool Live() => canInteract?.Invoke() != false;
        bool initializing = true;
        bool Editable() => Live() && !controllerPending && !initializing;
        var initialize = new List<Action>();
        refreshImageStatus = null;
        ErrorContentY = null;
        double y = includePages ? AddPageNavigation(composer, width, canInteract: canInteract) : 0;
        double rowHeight = embedded ? 60 : 40;
        double labelHeight = embedded ? 46 : 26;
        bool stacked = embedded && width < 480;
        double controlX = stacked ? 0 : embedded ? width * 0.55 + 10 : 315;
        double controlWidth = width - controlX;
        double labelWidth = stacked ? width : controlX - 15;
        double controlY = y;
        double lastLabelHeight = 0;
        int textSequence = 0;
        void DefinedChoice(string label, string key)
        {
            var values = RendererChoices.Get(key, draft["Upscaler"]?.GetValue<string>() == "xess");
            Choice(label, key, values.Select(item => item.Value).ToArray(), values.Select(item => item.Label).ToArray());
        }
        switch (page)
        {
            case 0:
                if (RendererChoices.VendorProviders)
                {
                    DefinedChoice("Upscaler", "Upscaler");
                    DefinedChoice("Quality", "UpscalerQuality");
                }
                Slider("Render scale (%)", "RenderScale");
                Switch("Temporal anti-aliasing", "Taa");
                Slider("TAA sharpness (%)", "TaaSharpness");
                Slider("TAA mip bias", "TaaMipBias");
                if (RendererChoices.VendorProviders) Slider("Upscaler mip adjustment (%)", "UpscalerLodBiasOffset");
                const string imageStatusKey = "vulkanstory-image-status";
                string ImageStatus() => AppContext.GetData("VulkanStory.Runtime.Presentation") is Func<string> read
                    ? string.Join("\n", read().Split('\n').Where(line =>
                        line.StartsWith("Upscaler", StringComparison.Ordinal) || line.StartsWith("Render resolution:", StringComparison.Ordinal))) +
                        "\n" + (AppContext.GetData("VulkanStory.Runtime.FpsText") is Func<string> fps ? fps() : "FPS unavailable")
                    : "Renderer runtime is unavailable.";
                Label(ImageStatus(), CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width, 125), imageStatusKey);
                refreshImageStatus = () => composer.GetDynamicText(imageStatusKey).SetNewText(ImageStatus(), false, true, false);
                y += Math.Max(135, lastLabelHeight + 10);
                Text(RendererChoices.VendorProviders
                    ? "Quality controls the selected upscaler's internal resolution. Render scale applies when the upscaler is Off. FPS gains can be limited by VSync, a frame cap or the CPU."
                    : "Render scale applies to the whole image. FPS gains can be limited by VSync, a frame cap or the CPU.");
                break;
            case 1:
                if (RendererChoices.VendorProviders)
                {
                    DefinedChoice("Frame generation", "FrameGeneration");
                    Slider("Requested multiplier", "FrameGenerationMultiplier");
                    DefinedChoice("Low latency", "LowLatencyMode");
                }
                Switch("Show FPS counter", "ShowFpsCounter");
                if (RendererChoices.VendorProviders)
                {
                    Text("Available providers and multipliers depend on your GPU. Unsupported settings fall back automatically.");
                    Text("With frame generation selected, low latency remains enabled. Boost depends on the provider.");
                }
                else
                {
                    Text("Frame generation and vendor latency need provider SDKs that this platform does not have.");
                }
                break;
            case 2:
                DefinedChoice("Ambient occlusion", "AmbientOcclusion");
                DefinedChoice("AO quality", "AmbientOcclusionPreset");
                Switch("Show ambient occlusion", "AmbientOcclusionDebugView");
                Switch("Reduce god-ray sample cost", "GodRaysSampleCap");
                Switch("Handheld shadow tier", "HandheldShadowTier");
                DebugView();
                break;
            case 3:
                Switch("Enable VulkanStory (restart)", "Enabled");
                if (RendererChoices.VendorProviders)
                {
                    Switch("OpenXR headset / stereo (restart)", "OpenXr");
                    Text("VR currently uses ordinary rendering. TAA, upscaling and frame generation remain desktop features.");
                }
                Switch("Controllers", "ControllerEnabled");
                Switch("Touch input", "TouchEnabled");
                Switch("Native shaders (restart)", "NativeShaders");
                if (RendererChoices.VendorProviders) Switch("Streamline (restart)", "Streamline");
                Text("Controller remapping, gyro and haptics are available from the controller settings panel.");
                composer.AddSmallButton("Save and open controller settings", () => Editable() ? SaveAndOpenController() : true,
                    ElementBounds.Fixed(0, y, width, 30));
                y += 42;
                break;
            case 4:
                Switch("Chunk visibility walk", "ChunkVisibilityWalk");
                Switch("Flood-fill chunk visibility", "BfsChunkVisibility");
                Switch("Indexed flood-fill visibility", "IndexedChunkVisibility");
                Switch("GPU terrain occlusion culling", "TerrainOcclusionCull");
                Switch("Entity occlusion culling", "EntityOcclusionCull");
                Switch("Skip foliage in far shadows", "ShadowFarFoliage");
                Switch("Cull distant entity shadows", "EntityShadowCull");
                Slider("Entity shadow distance (blocks)", "EntityShadowDistance");
                Switch("Skip distant particle spawns", "ParticleDistanceGate");
                Switch("Limit particle quantities", "ParticleBudgets");
                Slider("Particle density (%)", "ParticleQuantityPercent");
                Slider("Particles per spawn (0: unlimited)", "ParticleSpawnLimit");
                Slider("Live particles per pool (0: unlimited)", "ParticlePoolLimit");
                Switch("Merge settled drops sooner (singleplayer)", "EarlyItemMerge");
                Text("Particle limits apply live to cubes and quads, including mod effects. Effects marked to ignore user settings are preserved. Lowering the live limit stops new spawns until the pool falls below it; existing particles finish normally. Drop merging preserves matching attributes, player ownership and the game's automatic stack rules.");
                Switch("Cull distant dropped items", "DroppedItemCull");
                Slider("Dropped item distance (blocks)", "DroppedItemDistance");
                Switch("Skip unchanged chunk upload sorting", "UploadQueueSortSkip");
                Switch("Reuse chunk mesh buffers", "MeshPartPool");
                Switch("Parallel chunk reads (next world)", "ParallelChunkReads");
                DefinedChoice("Worldgen workers", "WorldgenWorkers");
                Switch("Allocation-free animation checks", "AnimationLinqFree");
                Switch("Falling block mesh fix", "FallingBlockMeshFix");
                Switch("Villager outfit cache", "OutfitShapeCache");
                Switch("Reuse inventory render info", "GuiItemRenderInfoReuse");
                Switch("Cache weather wind samples", "WeatherWindSampleCache");
                Switch("Animated block distance LOD", "AnimatedBlockLod");
                Switch("Cache dynamic light scans", "DynamicLightScanCache");
                Switch("Allocation-free nearest lights", "NearestLightSelection");
                Switch("Spread entity mesh rebuilds", "EntityRetessellationBudget");
                Switch("Reuse pathfinding nodes", "PathfindingNodePool");
                Switch("Batch entity lighting", "EntityLightBatch");
                Switch("SIMD frustum tests", "SimdFrustumTests");
                Switch("Cache outfit animator builds", "OutfitAnimatorCache");
                Switch("Parallel Terrain columns", "ParallelTerrainGeneration");
                Switch("Reuse physics client lists", "PhysicsCollectionReuse");
                Switch("Batch physics activation capacity", "PhysicsActivationBatch");
                Switch("Distant chisel mesh LOD", "ChiselMeshLod");
                Slider("Chisel LOD distance (blocks)", "ChiselLodDistance");
                Switch("World map page cache", "WorldMapPageCache");
                Switch("Reuse occlusion hierarchy storage", "OcclusionStorageReuse");
                Switch("Cache translation lookups", "TranslationLookupCache");
                Switch("Reuse audio decode buffers", "AudioDecodeBufferReuse");
                Switch("Cache fruiting-bush shape parsing", "ShapeParseCache");
                Switch("Prepare outfit metadata off-thread", "OutfitMetadataPreparation");
                Switch("Reuse cloud tile coordinates", "CloudCoordinateReuse");
                string unavailable = AppContext.GetData("VulkanStory.Runtime.Presentation") is Func<string> performance
                    ? string.Join("\n", performance().Split('\n').Where(line => line.Contains(": unavailable (", StringComparison.Ordinal)).Select(line => line.Trim()))
                    : "";
                if (unavailable.Length != 0) Text("Unavailable in this game version:\n" + unavailable);
                Text("These ports trade small visual differences or none at all for smoother frames. Parallel chunk reads apply at the next world load; the other controls apply live. Worldgen and chunk reads affect singleplayer only. The map cache retains the normal renderer when a view exceeds its resident-page budget.");
                break;
            default:
                string status = AppContext.GetData("VulkanStory.Runtime.Presentation") is Func<string> read
                    ? read() : "Renderer runtime is unavailable.";
                foreach (string line in status.Split('\n'))
                {
                    Label(line, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width, embedded ? 65 : 35));
                    y += embedded ? lastLabelHeight + 12 : Math.Max(40, lastLabelHeight + 10);
                }
                Text("Input preparation is not proof that generated frames were presented. Reported counts are shown when available.");
                break;
        }
        if (errorMessage != null) { ErrorContentY = y; Text(errorMessage); }
        if (controllerPending) Text("Applying settings and opening controller settings...");
        Text(controllerPending
            ? "Settings have been saved. Cancel stops opening the controller panel; saved settings remain applied."
            : "Changes apply immediately. Save keeps them; Cancel restores the previous settings. Options marked restart still require a restart.");
        if (includeFooter) AddFooter(composer, width, y + 8, canInteract);
        height = y + (includeFooter ? 46 : 0);
        return () => { foreach (var action in initialize) action(); initializing = false; };

        void Text(string label)
        { Label(label, CairoFont.WhiteSmallText(), ElementBounds.Fixed(0, y, width, embedded ? 75 : 55)); y += Math.Max(embedded ? 85 : 65, lastLabelHeight + 10); }
        GuiComposer Label(string text, CairoFont font, ElementBounds bounds, string? elementKey = null)
        {
            lastLabelHeight = bounds.fixedHeight;
            controlY = y;
            if (!embedded && elementKey == null) return composer.AddStaticText(text, font, bounds);
            if (string.IsNullOrEmpty(text)) return composer;
            var label = new GuiElementDynamicText(composer.Api, text, font, bounds);
            composer.AddInteractiveElement(label, elementKey ?? "vulkanstory-text-" + textSequence++);
            bounds.CalcWorldBounds();
            label.AutoHeight();
            lastLabelHeight = bounds.fixedHeight;
            controlY = y + (stacked ? lastLabelHeight + 6 : 0);
            return composer;
        }
        void DebugView()
        {
            string[] labels = ["Off", "Motion vectors", "Reactive mask", "Writer validity", "Motion overlay"];
            int value = Math.Clamp(draft["TaaDebugView"]?.Deserialize<int>() ?? 0, 0, labels.Length - 1);
            Label("Temporal debug view", CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, labelWidth, labelHeight))
                .AddSmallButton(labels[value], () => { if (Editable()) { draft["TaaDebugView"] = (value + 1) % labels.Length; Preview(); refresh = true; } return true; },
                    ElementBounds.Fixed(controlX, controlY, controlWidth, 28));
            NextRow();
        }
        void NextRow() => y += stacked ? lastLabelHeight + 46 : Math.Max(rowHeight, lastLabelHeight + 12);
        void Switch(string label, string key)
        {
            bool value = draft[key]?.GetValue<bool>() ?? false;
            Label(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, labelWidth, labelHeight))
                .AddSwitch(next => { if (Editable()) { draft[key] = next; Preview(); } }, ElementBounds.Fixed(controlX, controlY, 35, 26), key);
            initialize.Add(() => composer.GetSwitch(key).SetValue(value)); NextRow();
        }
        void Slider(string label, string key)
        {
            // Normalization, the chat command and this slider share one range.
            RendererRange range = RendererChoices.Range(key);
            int min = range.SliderMin, max = range.SliderMax, step = range.Step;
            float scale = range.Scale;
            int value = (int)MathF.Round((draft[key]?.Deserialize<float>() ?? 0f) * scale);
            string? valueKey = key == "FrameGenerationMultiplier" ? "vulkanstory-multiplier-value" : null;
            string Caption(int current) => valueKey == null ? label : label + " (" + current + "x)";
            Label(Caption(Math.Clamp(value, min, max)), CairoFont.WhiteSmallishText(),
                ElementBounds.Fixed(0, y, labelWidth, labelHeight), valueKey)
                .AddSlider(next =>
                {
                    if (!Editable()) return true;
                    draft[key] = next / scale;
                    Preview();
                    if (valueKey != null)
                        composer.GetDynamicText(valueKey).SetNewText(Caption(next), false, true, false);
                    return true;
                }, ElementBounds.Fixed(controlX, controlY, controlWidth, 26), key);
            initialize.Add(() => composer.GetSlider(key).SetValues(Math.Clamp(value, min, max), min, max, step)); NextRow();
        }
        void Choice(string label, string key, string[] values, string[] labels)
        {
            int index = Array.FindIndex(values, value => value == draft[key]?.GetValue<string>());
            if (index < 0) index = 0;
            Label(label, CairoFont.WhiteSmallishText(), ElementBounds.Fixed(0, y, labelWidth, labelHeight))
                .AddSmallButton(labels[index], () =>
                {
                    if (!Editable()) return true;
                    draft[key] = values[(index + 1) % values.Length];
                    // XeSS-only quality modes fall back when another provider is selected.
                    if (key == "Upscaler" && draft[key]?.GetValue<string>() != "xess" &&
                        draft["UpscalerQuality"]?.GetValue<string>() is string quality &&
                        RendererChoices.Contains("UpscalerQuality", quality, xess: true) &&
                        !RendererChoices.Contains("UpscalerQuality", quality))
                        draft["UpscalerQuality"] = "quality";
                    Preview();
                    refresh = true; return true;
                },
                    ElementBounds.Fixed(controlX, controlY, controlWidth, 28));
            NextRow();
        }
    }
    /// <summary>Adds guarded Cancel and Save buttons; Save is disabled while a controller transition is pending.</summary>
    internal GuiComposer AddFooter(GuiComposer composer, double width, double y, Func<bool>? canInteract = null)
    {
        bool Live() => canInteract?.Invoke() != false;
        return composer.AddSmallButton("Cancel", () => Live() ? Cancel() : true,
                ElementBounds.Fixed(width - 260, y, 120, 30))
            .AddSmallButton("Save", () => Live() && !controllerPending ? Save() : true,
                ElementBounds.Fixed(width - 120, y, 120, 30));
    }
}
