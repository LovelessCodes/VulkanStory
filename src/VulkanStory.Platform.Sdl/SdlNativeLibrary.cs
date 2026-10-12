using System.Reflection;
using System.Runtime.InteropServices;

namespace VulkanStory.Platform.Sdl;

/// <summary>Explicit SDL3 import resolution for the package-selected native runtime and integration assemblies.</summary>
/// <remarks>Resolver registration does not itself load SDL; native loading is deferred until a P/Invoke requests it.</remarks>
public static class SdlNativeLibrary
{
    private static readonly HashSet<Assembly> registeredImports = new();
    // The assembly is shipped in VulkanStory/managed and SDL in VulkanStory/native/<rid>.
    // Registration is explicit before the first SDL call. Loading this assembly
    // during bootstrap does not run a module initializer or load SDL itself.
    static SdlNativeLibrary() => NativeLibrary.SetDllImportResolver(
        typeof(SdlNativeLibrary).Assembly, Resolve);

    /// <summary>Forces this type's resolver registration before an SDL call without performing native initialization.</summary>
    internal static void EnsureRegistered() { }

    // Controller P/Invokes remain in the game integration assembly. They use
    // the same package-selected SDL binary as the window/event implementation.
    /// <summary>Registers the shared SDL resolver once for another assembly containing SDL3 P/Invokes.</summary>
    /// <param name="assembly">Integration assembly whose imports must use the same native SDL runtime.</param>
    /// <remarks>An assembly with an independently installed import resolver can cause NativeLibrary registration to throw.</remarks>
    public static void RegisterAssemblyImports(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (assembly == typeof(SdlNativeLibrary).Assembly) return;
        lock (registeredImports)
        {
            if (registeredImports.Contains(assembly)) return;
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
            registeredImports.Add(assembly);
        }
    }

    /// <summary>Loads SDL from the importing assembly's package-relative RID directory, preserving ordinary probing for standalone tools.</summary>
    /// <remarks>A recognized deployed package refuses missing or unloadable private SDL binaries.</remarks>
    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "SDL3", StringComparison.Ordinal)) return 0;
        string? binary = OperatingSystem.IsWindows() ? "SDL3.dll" :
            OperatingSystem.IsMacOS() ? "libSDL3.dylib" :
            OperatingSystem.IsLinux() ? "libSDL3.so" : null;
        if (binary is null) return 0;
        string? rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => OperatingSystem.IsWindows() ? "win-x64" :
                OperatingSystem.IsMacOS() ? "osx-x64" : "linux-x64",
            Architecture.Arm64 => OperatingSystem.IsWindows() ? "win-arm64" :
                OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-arm64",
            _ => null
        };
        if (rid is null || string.IsNullOrEmpty(assembly.Location)) return 0;
        string managedDirectory = Path.GetDirectoryName(assembly.Location)!;
        string packageRoot = Path.GetFullPath(Path.Combine(managedDirectory, ".."));
        string candidate = Path.Combine(packageRoot, "native", rid, binary);
        if (!File.Exists(candidate))
        {
            // Standalone developer tests may use a system SDL. A deployed
            // VulkanStory package must use its own matching native runtime.
            if (string.Equals(Path.GetFileName(managedDirectory), "managed", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Path.GetFileName(packageRoot), "VulkanStory", StringComparison.OrdinalIgnoreCase))
                throw new DllNotFoundException($"VulkanStory SDL3 runtime is missing: {candidate}");
            return 0;
        }
        DllImportSearchPath? flags = OperatingSystem.IsWindows()
            ? DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories : null;
        if (NativeLibrary.TryLoad(candidate, assembly, flags, out nint handle)) return handle;
        throw new DllNotFoundException($"VulkanStory SDL3 runtime could not be loaded: {candidate}");
    }
}
