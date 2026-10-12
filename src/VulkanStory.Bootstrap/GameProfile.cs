using System.Security.Cryptography;
using System.Text.Json;

namespace VulkanStory.Bootstrap;

/// <summary>Official client metadata and SHA256 file identities required before startup patches are attempted.</summary>
/// <param name="Schema">Profile schema version; the loader currently accepts one.</param>
/// <param name="Id">Stable integration-profile identity.</param>
/// <param name="GameVersion">Recorded official game version.</param>
/// <param name="RuntimeMajor">Required .NET runtime major version.</param>
/// <param name="Files">Game-relative files and their expected SHA256 hex digests.</param>
internal sealed record GameProfile(int Schema, string Id, string GameVersion, int RuntimeMajor,
    Dictionary<string, string> Files)
{
    /// <summary>Reads a bounded profile JSON file and rejects unsupported or empty profile data.</summary>
    /// <param name="path">Profile JSON path.</param>
    /// <returns>The deserialized schema-one profile.</returns>
    /// <exception cref="InvalidDataException">The profile exceeds 64 KiB or has unsupported/empty metadata.</exception>
    internal static GameProfile Load(string path)
    {
        using FileStream file = File.OpenRead(path);
        if (file.Length > 64 * 1024) throw new InvalidDataException("Game profile exceeds the size limit.");
        GameProfile profile = JsonSerializer.Deserialize<GameProfile>(file,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Missing game profile.");
        if (profile.Schema != 1 || string.IsNullOrWhiteSpace(profile.Id) || profile.Files is null || profile.Files.Count == 0)
            throw new InvalidDataException("Unsupported or empty game profile.");
        return profile;
    }

    /// <summary>Hashes every named official game file and refuses mismatches or paths outside the game root.</summary>
    /// <param name="gameDirectory">Official installation root against which relative profile paths are resolved.</param>
    /// <param name="redirects">Optional profile-file replacements, such as an apphost an installer renamed.</param>
    /// <exception cref="InvalidDataException">A path escapes the root or a file does not match its recorded digest.</exception>
    internal void VerifyFiles(string gameDirectory, IReadOnlyDictionary<string, string>? redirects = null)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)) + Path.DirectorySeparatorChar;
        foreach ((string relative, string expected) in Files)
        {
            string target = redirects is not null && redirects.TryGetValue(relative, out string? redirected)
                ? redirected : relative;
            string path = Path.GetFullPath(Path.Combine(root, target));
            if (Path.IsPathRooted(target) || !path.StartsWith(root,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidDataException($"Profile path is outside the game directory: {target}");
            using FileStream file = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(file));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Game profile {Id} does not match {relative}. No startup patches were applied.");
        }
    }
}
