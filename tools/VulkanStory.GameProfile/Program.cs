using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using VulkanStory.Game;

// CLI entry: verifies the official installation hashes against the embedded startup
// IL profile, validates platform metadata, and writes a method-operand inventory.
// This metadata check does not launch the game or establish startup/render acceptance.
// Exit codes: 0 for a matching inventory, 1 for a captured failure, 2 for invalid arguments.
bool inspectPerformance = args.Length == 7 && args[6] == "--performance-patches";
if ((args.Length != 6 && !inspectPerformance) || args[0] != "--game-directory" || args[2] != "--profile" || args[4] != "--output")
{
    Console.Error.WriteLine("Usage: VulkanStory.GameProfile --game-directory <official game dir> --profile <profile.json> --output <inventory.json> [--performance-patches]");
    return 2;
}

try
{
    string gameDirectory = Path.GetFullPath(args[1]);
    string profilePath = Path.GetFullPath(args[3]);
    string outputPath = Path.GetFullPath(args[5]);

    using JsonDocument profile = JsonDocument.Parse(File.ReadAllText(profilePath));
    string profileId = profile.RootElement.GetProperty("id").GetString() ??
        throw new InvalidDataException("Profile id is missing.");
    // The file profile is per platform; the embedded startup IL profile is per game
    // assembly, and 1.22.7 ships the same client assembly on every desktop platform.
    if (profile.RootElement.GetProperty("files").GetProperty("VintagestoryLib.dll").GetString() != StartupIlProfile.GameAssemblySha256)
        throw new InvalidDataException("File profile and embedded startup IL profile target different game assemblies.");
    foreach (JsonProperty file in profile.RootElement.GetProperty("files").EnumerateObject())
    {
        string path = Path.Combine(gameDirectory, file.Name.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) throw new FileNotFoundException("Profile file is missing.", path);
        using FileStream input = File.OpenRead(path);
        string actual = Convert.ToHexString(SHA256.HashData(input));
        if (!string.Equals(actual, file.Value.GetString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Official profile hash mismatch: " + file.Name);
    }

    AssemblyLoadContext.Default.Resolving += (_, name) =>
    {
        foreach (string directory in new[] { gameDirectory, Path.Combine(gameDirectory, "Lib"), Path.Combine(gameDirectory, "Mods") })
        {
            string path = Path.Combine(directory, name.Name + ".dll");
            if (File.Exists(path)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
        }
        return null;
    };

    string gameAssemblyPath = Path.Combine(gameDirectory, "VintagestoryLib.dll");
    Assembly game = AssemblyLoadContext.Default.LoadFromAssemblyPath(gameAssemblyPath);
    IReadOnlyList<StartupMethodInventory> methods = StartupIlInventory.CaptureProfile1227(game);
    StartupIlProfile.VerifyProfile1227(methods);
    GamePlatformBindingProfile.Validate1227();

    var performance = new List<object>();
    int performanceFailures = 0;
    if (inspectPerformance)
    {
        Assembly essentials = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDirectory, "Mods", "VSEssentials.dll"));
        Assembly survival = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(gameDirectory, "Mods", "VSSurvivalMod.dll"));
        performance = PerformanceInstallationCheck.Run(essentials, survival, out performanceFailures);
    }

    string? parent = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(new
    {
        profileId,
        gameAssemblySha256 = profile.RootElement.GetProperty("files").GetProperty("VintagestoryLib.dll").GetString(),
        methods,
        performance,
    }, new JsonSerializerOptions { WriteIndented = true }));

    foreach (StartupMethodInventory method in methods)
    {
        int windowUses = method.Uses.Count(use =>
            use.Member.Contains("Window", StringComparison.Ordinal) ||
            use.Member.Contains("GLFW", StringComparison.Ordinal) ||
            use.Member.Contains("ScreenManager", StringComparison.Ordinal));
        Console.WriteLine($"{method.Event}: IL={method.IlLength}, member operands={method.Uses.Count}, window-related={windowUses}");
    }
    Console.WriteLine("Matched pinned startup IL anchors and saved inventory: " + outputPath);
    Console.WriteLine("Matched original platform input/frame/close binding metadata.");
    return performanceFailures == 0 ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
