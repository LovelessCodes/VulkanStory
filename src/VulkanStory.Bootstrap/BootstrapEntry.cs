using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace VulkanStory.Bootstrap;

/// <summary>Validates a supported client profile and installs managed integration before game startup.</summary>
/// <remarks>Graphics ownership remains with the original startup path until the integration commits routing.</remarks>
internal static class BootstrapEntry
{
    private static int started;
    private static BootstrapDependencies? dependencies;

    /// <summary>Attempts process-local bootstrap once, installs dependency resolution, and invokes the integration entry.</summary>
    /// <remarks>Ordinary failures unregister this resolver and publish a bypass; headless failures are rethrown.</remarks>
    internal static void Initialize()
    {
        if (Interlocked.Exchange(ref started, 1) != 0) return;
        BootstrapTrace trace = BootstrapTrace.Create();
        trace.Write("managed.bootstrap.enter", "Early runtime entry; graphics ownership follows complete profile preparation.");
        AppContext.SetData("VulkanStory.Bootstrap.Status", "initializing");
        try
        {
            string ownPath = typeof(BootstrapEntry).Assembly.Location;
            StartupHookEnvironment.RemoveOwnHook(ownPath);
            string gameDirectory = Path.GetFullPath(AppContext.BaseDirectory);
            string? executable = Path.GetFileName(Environment.ProcessPath);
            bool windows = OperatingSystem.IsWindows();
            bool linux = OperatingSystem.IsLinux();
            bool macos = OperatingSystem.IsMacOS();
            StringComparison processComparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            bool clientEntry = Assembly.GetEntryAssembly()?.GetName().Name == "Vintagestory";
            bool harness = Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1" &&
                string.Equals(executable, windows ? "dotnet.exe" : "dotnet", processComparison);
            if ((!windows && !linux && !macos) ||
                RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64) || !clientEntry ||
                (!string.Equals(executable, windows ? "Vintagestory.exe" : "Vintagestory", processComparison) && !harness))
            {
                SetStatus(trace, "bypassed", "Not a supported desktop client process.");
                return;
            }

            if (!IsBootstrapEnabled(Path.Combine(gameDirectory, "VulkanStory", "loader.ini")))
            {
                SetStatus(trace, "bypassed", "Disabled by VulkanStory/loader.ini.");
                return;
            }
            string managedDirectory = Path.GetDirectoryName(ownPath)!;
            string profileId = windows ? "vs-1.22.7-win-x64" :
                macos ? (RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                    ? "vs-1.22.7-osx-arm64" : "vs-1.22.7-osx-x64") :
                "vs-1.22.7-linux-x64";
            GameProfile profile = GameProfile.Load(Path.Combine(managedDirectory, "profiles", profileId + ".json"));
            if (profile.Id != profileId) throw new InvalidDataException("Game profile does not match the selected platform.");
            if (Environment.Version.Major != profile.RuntimeMajor)
                throw new InvalidDataException($"Expected .NET {profile.RuntimeMajor}, found {Environment.Version}.");
            profile.VerifyFiles(gameDirectory);
            trace.Write("managed.profile.accepted", profile.Id);

            dependencies = new BootstrapDependencies(managedDirectory, gameDirectory, trace.Write);
            dependencies.Register();
            string integrationPath = Path.Combine(managedDirectory, "VulkanStory.Game.dll");
            if (AssemblyName.GetAssemblyName(integrationPath).Version != typeof(BootstrapEntry).Assembly.GetName().Version)
                throw new InvalidDataException("Bootstrap and game integration payload versions differ.");
            Assembly integration = AssemblyLoadContext.Default.LoadFromAssemblyPath(integrationPath);
            MethodInfo install = integration.GetType("VulkanStory.Game.RuntimeBootstrap", throwOnError: true)!
                .GetMethod("Install", BindingFlags.Public | BindingFlags.Static,
                    new[] { typeof(string), typeof(Action<string, string>) })
                ?? throw new MissingMethodException("VulkanStory.Game.RuntimeBootstrap.Install");
            install.Invoke(null, new object[] { gameDirectory, (Action<string, string>)trace.Write });
            SetStatus(trace, "observing", "Process runtime loaded; graphics activate only with a complete registered startup profile.");
            AppDomain.CurrentDomain.ProcessExit += (_, _) => trace.Write("managed.process.exit", "");
        }
        catch (Exception error)
        {
            dependencies?.Unregister();
            Exception cause = (error as TargetInvocationException)?.InnerException ?? error;
            SetStatus(trace, "bypassed", cause.ToString());
            // A harness must not fall back to an ordinary visible OpenGL client.
            if (Environment.GetEnvironmentVariable("VULKANSTORY_HEADLESS") == "1")
                throw new InvalidOperationException("Headless bootstrap failed; refusing visible fallback.", cause);
        }
    }

    /// <summary>Reads the bounded package-local enable switch before integration or graphics dependencies load.</summary>
    /// <remarks>Missing configuration preserves the native Windows loader's enabled default; the script-activated platforms require the file.</remarks>
    private static bool IsBootstrapEnabled(string path)
    {
        if (!File.Exists(path)) return OperatingSystem.IsWindows();
        if (new FileInfo(path).Length > 64 * 1024) throw new InvalidDataException("Loader configuration exceeds the size limit.");
        bool bootstrapSection = false;
        bool enabled = true;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                bootstrapSection = line[1..^1].Trim().Equals("Bootstrap", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            int separator = line.IndexOf('=');
            if (!bootstrapSection || separator < 0 ||
                !line[..separator].Trim().Equals("Enabled", StringComparison.OrdinalIgnoreCase)) continue;
            if (!int.TryParse(line[(separator + 1)..].Trim(), out int value))
                throw new InvalidDataException("Bootstrap Enabled must be an integer (0 disables activation).");
            enabled = value != 0;
        }
        return enabled;
    }

    /// <summary>Publishes the bootstrap state for the late mod entry and appends its diagnostic marker.</summary>
    private static void SetStatus(BootstrapTrace trace, string status, string reason)
    {
        AppContext.SetData("VulkanStory.Bootstrap.Status", status);
        trace.Write("managed.bootstrap." + status, reason);
    }
}
