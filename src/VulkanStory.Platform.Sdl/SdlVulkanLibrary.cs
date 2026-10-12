using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VulkanStory.Platform.Sdl;

/// <summary>One Vulkan runtime shared by SDL's surface path and the renderer's own device.</summary>
/// <remarks>
/// On macOS SDL prefers MoltenVK while the renderer's loader resolution prefers libvulkan; creating an
/// instance through one implementation and a surface through the other crashes inside
/// <c>vkGetInstanceProcAddr</c>. The process resolves one runtime here, the renderer loads it explicitly
/// and <see cref="EnsurePinned" /> points SDL at the same file before its first Vulkan call.
/// </remarks>
public static class SdlVulkanLibrary
{
    private static readonly object Gate = new();
    private static string? resolved;
    private static bool resolvedOnce;
    private static bool pinned;

    /// <summary>Absolute path of the process's Vulkan runtime, or null when the platform resolves it by name.</summary>
    public static string? ResolvedPath
    {
        get
        {
            lock (Gate)
            {
                if (!resolvedOnce)
                {
                    resolved = Resolve();
                    resolvedOnce = true;
                }
                return resolved;
            }
        }
    }

    /// <summary>Points SDL at the process's Vulkan runtime before SDL loads one of its own.</summary>
    internal static void EnsurePinned()
    {
        string? path = ResolvedPath;
        if (path is null) return;
        lock (Gate)
        {
            if (pinned) return;
            pinned = true;
        }
        SdlWindowHost.PinVulkanLibrary(path);
    }

    /// <summary>Resolves the macOS Vulkan runtime: explicit override, package/assembly layout, then standard prefixes.</summary>
    /// <remarks>A VULKANSTORY_VULKAN_LOADER override names a file that must exist; a missing runtime returns null
    /// and leaves both SDL and the renderer on their own name-based resolution.</remarks>
    private static string? Resolve()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        string? configured = Environment.GetEnvironmentVariable("VULKANSTORY_VULKAN_LOADER");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string overridePath = Path.GetFullPath(configured);
            if (!File.Exists(overridePath))
            {
                throw new FileNotFoundException(
                    "VULKANSTORY_VULKAN_LOADER does not name an existing file.", overridePath);
            }
            return overridePath;
        }
        foreach (string directory in CandidateDirectories())
        {
            foreach (string name in LibraryNames)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        foreach (string directory in StandardDirectories)
        {
            foreach (string name in LibraryNames)
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>macOS runtime names in preference order: the loader first, the portability implementation second.</summary>
    private static readonly string[] LibraryNames = ["libvulkan.1.dylib", "libvulkan.dylib", "libMoltenVK.dylib"];

    /// <summary>Package prefixes used when no copy travels with the payload.</summary>
    private static readonly string[] StandardDirectories = ["/opt/homebrew/lib", "/usr/local/lib"];

    /// <summary>Directories that may hold a packaged or development copy of the runtime.</summary>
    private static IEnumerable<string> CandidateDirectories()
    {
        string application = AppContext.BaseDirectory;
        if (application.Length != 0) yield return application;
        string assembly = typeof(SdlVulkanLibrary).Assembly.Location;
        if (string.IsNullOrEmpty(assembly)) yield break;
        string managed = Path.GetDirectoryName(assembly)!;
        yield return managed;
        string package = Path.GetFullPath(Path.Combine(managed, ".."));
        string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        yield return Path.Combine(package, "native", rid);
    }
}
