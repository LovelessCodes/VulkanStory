using Vintagestory.Client.NoObf;

namespace VulkanStory.Game;

// The profile supplies all mandatory startup groups, including complete GL
// coverage. Existing graphics subsets do not become a complete profile here.
/// <summary>Complete startup patch coverage and a deferred settings/data-path factory; the factory runs at the first window request.</summary>
/// <remarks>Performance features are optional and install separately after commit; they never make the startup transaction fail.</remarks>
internal sealed record GameStartupPlan(StartupPatchGroup[] Groups, Func<GameSessionServices> CreateServices,
    VulkanStory.Game.Performance.PerformanceFeature[]? Performance = null);

/// <summary>Shared early/ordinary-mod entry into the one process runtime.</summary>
public static class RuntimeBootstrap
{
    private static ProcessRuntime? current;
    public static string Status => current?.Status ?? "not-loaded";
    public static bool IsActive => current?.IsActive == true;
    internal static ProcessRuntime Current => current ?? throw new InvalidOperationException("The early runtime is not loaded.");

    /// <summary>Installs the process runtime and complete official startup routing before the game entry point executes.</summary>
    /// <param name="gameDirectory">Official game installation used to verify assembly locations.</param>
    /// <param name="trace">Diagnostic sink receiving event names and details.</param>
    /// <remarks>A second installation is rejected. Failures publish failed status and propagate; trace callbacks from the runtime notification path are best effort.</remarks>
    public static void Install(string gameDirectory, Action<string, string> trace)
    {
        if (current != null) throw new InvalidOperationException("The process runtime is already installed.");
        var runtime = new ProcessRuntime(trace);
        current = runtime;
        try
        {
            StartupObservation.StageEntering += runtime.NoteStartup;
            StartupObservation.Install(gameDirectory, trace);
            VersionProfile1227.Prepare(runtime, gameDirectory);
            RuntimeControlBridge.Install(runtime);
            runtime.Notify("runtime.installed", "Original 1.22.7 routing prepared before Main; settings resolve at the first window request.");
        }
        catch
        {
            StartupObservation.StageEntering -= runtime.NoteStartup;
            runtime.Publish("failed");
            throw;
        }
    }
    internal static void Configure(GameStartupPlan plan) => Current.Prepare(plan);
}

/// <summary>Owns startup routing and the SDL/Vulkan session; transitions run on the thread that created the runtime.</summary>
internal sealed partial class ProcessRuntime(Action<string, string> trace)
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private GameStartupPlan? plan;
    private GameSessionServices? selectedServices;
    private StartupRoutingTransaction? routing;
    private GameRenderSession? session;
    private bool windowRequested;
    private ClientPlatformWindows? platform;
    internal bool HasCommitted { get; private set; }
    internal string Status { get; private set; } = "loading";
    internal bool IsActive => routing?.RoutingEnabled == true && session is { Stopping: false };
    internal bool CanCreateWindow => routing?.State == StartupRoutingState.Prepared;
    internal GameRenderSession Session => session ?? throw new InvalidOperationException("No SDL/Vulkan session exists.");
    internal StartupRoutingTransaction Routing => routing ?? throw new InvalidOperationException("A complete startup profile is not prepared.");
    internal void NoteStartup(string stage)
    {
        if (stage is "game.window.request" or "game.window.construct") windowRequested = true;
    }
    internal void Publish(string status)
    {
        Status = status;
        AppContext.SetData("VulkanStory.Runtime.Status", status);
    }
    /// <summary>Sends a runtime diagnostic without allowing a sink exception to change graphics ownership.</summary>
    /// <param name="name">Diagnostic event name.</param>
    /// <param name="detail">Event payload text.</param>
    internal void Notify(string name, string detail)
    {
        try { trace(name, detail); }
        catch { /* Diagnostics cannot change graphics ownership or rollback. */ }
    }
    /// <summary>Validates and installs a complete startup plan while graphics routing remains dormant.</summary>
    /// <param name="completePlan">All mandatory patch groups and the deferred service factory.</param>
    /// <remarks>Must run once on the runtime owner thread before the first window request.</remarks>
    internal void Prepare(GameStartupPlan completePlan)
    {
        RequireOwner();
        ArgumentNullException.ThrowIfNull(completePlan);
        if (windowRequested || routing != null) throw new InvalidOperationException("Startup routing must be registered once before the first window request.");
        plan = completePlan;
        routing = new StartupRoutingTransaction(completePlan.Groups);
        try { routing.Prepare(); Publish("prepared"); }
        catch { Publish("failed"); throw; }
    }
    internal void CreateWindow(ClientPlatformWindows platform, string title, int width, int height)
    {
        RequireOwner();
        if (session != null || plan is null) throw new InvalidOperationException("The startup session is missing its plan or already exists.");
        try
        {
            Routing.Commit(() => session = GameRenderSession.Create(platform, Routing,
                selectedServices ?? throw new InvalidOperationException("Window services were not selected."), title, width, height));
            HasCommitted = true;
            InstallPerformance(platform);
            Publish("active");
            Notify("runtime.active", "SDL window and retained Vulkan device committed in the original game process.");
        }
        catch { Publish("failed"); throw; }
    }
    /// <summary>Installs the optional performance features once the renderer owns the window, before any world loads.</summary>
    /// <remarks>Each feature refuses independently; a failure here is logged and never changes graphics ownership.</remarks>
    private void InstallPerformance(ClientPlatformWindows owner)
    {
        if (plan?.Performance is not { Length: > 0 } features || selectedServices == null) return;
        try
        {
            Performance.PerformancePatchSet.Install(features, selectedServices.RendererSettings, owner.Logger);
            Notify("runtime.performance", Performance.PerformancePatchSet.Status);
        }
        catch (Exception error) { owner.Logger.Warning("VulkanStory: performance patches unavailable: {0}", error.Message); }
    }
    /// <summary>Records the original platform instance before the first SDL window is created.</summary>
    /// <param name="value">Original platform retained for the process session.</param>
    /// <remarks>A second distinct platform is rejected.</remarks>
    internal void RememberPlatform(ClientPlatformWindows value)
    {
        RequireOwner();
        if (platform != null && !ReferenceEquals(platform, value)) throw new InvalidOperationException("A process runtime cannot own two game platforms.");
        platform = value;
    }
    internal void CreateWindow(OpenTK.Windowing.Desktop.NativeWindowSettings settings)
    {
        RequireOwner();
        if (platform is null || plan is null || session != null) throw new InvalidOperationException("The original platform/session plan is not ready.");
        try
        {
            Routing.Commit(() => session = GameRenderSession.Create(platform, Routing,
                selectedServices ?? throw new InvalidOperationException("Window services were not selected."),
                settings.Title, settings.ClientSize.X, settings.ClientSize.Y, (int)settings.WindowState, (int)settings.WindowBorder));
            HasCommitted = true;
            InstallPerformance(platform);
            Publish("active");
            Notify("runtime.active", "First window owned by SDL; original game platform retained.");
        }
        catch { Publish("failed"); throw; }
    }
    /// <summary>Selects normalized settings at the first window boundary and honors original mod disablement.</summary>
    /// <returns>True when the prepared renderer should create the window; false after disabling routing for ordinary startup.</returns>
    /// <remarks>Headless mode rejects disabled rendering instead of permitting a visible fallback.</remarks>
    internal bool SelectWindowServices()
    {
        RequireOwner();
        if (!CanCreateWindow || plan == null) throw new InvalidOperationException("Window service selection requires a prepared profile.");
        bool modDisabled = RuntimeModDisablement.IsDisabled();
        if (!modDisabled)
        {
            selectedServices ??= plan.CreateServices() ?? throw new InvalidOperationException("Window service factory returned no owner.");
            if (selectedServices.RendererSettings.Settings.Enabled) return true;
        }
        if (HeadlessHarnessOptions.Enabled)
            throw new InvalidOperationException("Headless renderer is disabled; refusing visible OpenGL fallback.");
        Routing.Dispose();
        Publish("disabled");
        // macOS caps OpenGL at 4.1 while the game asks for 4.3: without this renderer the
        // client does not start, so the disable is recorded with that consequence.
        Notify("runtime.disabled", (modDisabled
            ? "VulkanStory is disabled in the game mod manager; original window startup continues."
            : "VulkanStory settings disable the renderer; original window startup continues.") +
            (OperatingSystem.IsMacOS()
                ? " This platform's OpenGL path cannot start the client (macOS provides 4.1, the game needs 4.3)."
                : ""));
        return false;
    }
    /// <summary>Finds the active session for the exact original platform instance.</summary>
    /// <param name="platform">Original platform identity expected by routed consumers.</param>
    /// <param name="found">Matching active session when the method returns true.</param>
    /// <returns>False while routing is inactive. An active identity mismatch throws.</returns>
    internal bool TrySession(ClientPlatformWindows platform, out GameRenderSession found)
    {
        found = null!;
        if (!IsActive) return false;
        if (session is null || !session.Matches(platform)) throw new InvalidOperationException("Active routing has no matching platform session.");
        found = session; return true;
    }
    internal void Run()
    {
        RequireOwner();
        if (!IsActive || session is null) throw new InvalidOperationException("SDL loop has no committed session.");
        session.Run();
    }
    /// <summary>Drains and releases the session before removing routing patches and published control delegates.</summary>
    /// <remarks>Runs on the owner thread. A session drain failure propagates before patch/window owners are cleared.</remarks>
    internal void Shutdown()
    {
        RequireOwner();
        // A drain failure retains the live window/patch owners for diagnosis.
        session?.Dispose();
        session = null;
        // Optional patches never block routing removal.
        try { Performance.PerformancePatchSet.Remove(); }
        catch (Exception error) { Notify("runtime.performance.remove-failed", error.Message); }
        routing?.Dispose();
        ClearPendingControls();
        RuntimeControlBridge.Remove();
        Publish("stopped");
        Notify("runtime.stopped", "Device drained before SDL window teardown and patch removal.");
    }
    private void RequireOwner()
    { if (ownerThread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Runtime transitions require the entry thread."); }
}
