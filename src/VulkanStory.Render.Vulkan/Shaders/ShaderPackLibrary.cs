using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VulkanStory.Render.Vulkan.Shaders;

/// <summary>
/// One loaded shader pack (docs/shader-packs.md, contract 1): the parsed manifest, the
/// compiled overrides as a <see cref="NativeShaderLibrary" />, and the per-slot failures.
///
/// Loading materializes a build tree under the shader cache - the pack's program sources,
/// renamed to their slot names, with the runtime's embedded copy of
/// <c>shaders/native/include</c> - and runs <see cref="NativeShaderBuilder" />, the same
/// compiler the offline tool and <c>VULKANSTORY_VK_SHADER_SOURCE</c> use. A slot whose
/// source does not compile is recorded in <see cref="Failures" /> and left to the shipped
/// program; it does not fail the pack or the other slots.
/// </summary>
internal sealed class ShaderPackLibrary
{
    /// <summary>Logical-name prefix of the embedded <c>shaders/native/include</c> files (the csproj sets it).</summary>
    public const string IncludeResourcePrefix = "VulkanStory.native.include.";

    /// <summary>Per-program source cap, the device's staged-source cap.</summary>
    public const int MaxProgramSourceBytes = 2 * 1024 * 1024;

    public ShaderPackManifest Manifest { get; }

    /// <summary>The pack's compiled overrides, looked up per program like the shipped library.</summary>
    public NativeShaderLibrary Library { get; }

    /// <summary>The pack directory, for the log.</summary>
    public string Origin { get; }

    /// <summary>SHA-256 over the manifest, resolved options, sources and compiler identity; the build tree's identity.</summary>
    public string ContentHash { get; }

    /// <summary>Slots whose source did not build, with the first errors; they fall back to the shipped program.</summary>
    public IReadOnlyDictionary<string, string> Failures { get; }

    /// <summary>Programs the pack replaces that built successfully.</summary>
    public int BuiltPrograms => Manifest.Programs.Count - Failures.Count;

    private ShaderPackLibrary(ShaderPackManifest manifest, NativeShaderLibrary library, string origin, string contentHash,
        Dictionary<string, string> failures)
    {
        Manifest = manifest;
        Library = library;
        Origin = origin;
        ContentHash = contentHash;
        Failures = failures;
    }

    /// <summary>
    /// Loads and builds the pack in <paramref name="packDirectory" />. Null with one reason when the
    /// directory, the manifest, a path, the include set or every program is unusable. A slot that is
    /// not a program of <paramref name="baseManifest" /> refuses the whole pack: contract 1 replaces
    /// shipped programs, it does not add new ones.
    /// </summary>
    public static ShaderPackLibrary? Load(string packDirectory, NativeShaderManifest baseManifest,
        ShaderCompiler compiler, string cacheRoot, out string reason)
    {
        if (!Directory.Exists(packDirectory))
        {
            reason = "no directory at " + packDirectory;
            return null;
        }

        string manifestPath = Path.Combine(packDirectory, ShaderPackManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            reason = "no " + ShaderPackManifest.FileName + " in " + packDirectory;
            return null;
        }

        ShaderPackManifest manifest;
        try
        {
            if (new FileInfo(manifestPath).Length > ShaderPackManifest.MaxBytes)
            {
                reason = ShaderPackManifest.FileName + " is larger than " + ShaderPackManifest.MaxBytes + " bytes";
                return null;
            }
            manifest = ShaderPackManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            reason = error.Message;
            return null;
        }

        try
        {
            if (Directory.Exists(Path.Combine(packDirectory, NativeShaderBuilder.IncludeDirectoryName)))
            {
                reason = "pack-local " + NativeShaderBuilder.IncludeDirectoryName + "/ overrides are not part of contract 1";
                return null;
            }

            foreach (ShaderPackProgram program in manifest.Programs)
            {
                if (baseManifest.FindProgram(program.Slot) == null)
                {
                    reason = "slot '" + program.Slot + "' is not a program of this runtime; contract 1 replaces shipped programs only";
                    return null;
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            reason = error.Message;
            return null;
        }

        var sources = new List<(string Slot, string Text)>();
        try
        {
            foreach (ShaderPackProgram program in manifest.Programs)
            {
                string path = Path.Combine(packDirectory, program.Source.Replace('\\', '/'));
                string text = File.ReadAllText(path);
                if (text.Length == 0)
                {
                    reason = "slot '" + program.Slot + "': " + program.Source + " is empty";
                    return null;
                }
                if (text.Length > MaxProgramSourceBytes)
                {
                    reason = "slot '" + program.Slot + "': " + program.Source + " is larger than " + MaxProgramSourceBytes + " bytes";
                    return null;
                }
                if (text.IndexOf('\0') >= 0)
                {
                    reason = "slot '" + program.Slot + "': " + program.Source + " contains a NUL byte";
                    return null;
                }
                sources.Add((program.Slot, text));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            reason = error.Message;
            return null;
        }

        Dictionary<string, string> defines;
        try
        {
            defines = manifest.ResolveDefines();
        }
        catch (InvalidDataException error)
        {
            reason = error.Message;
            return null;
        }

        string hash = ContentHashOf(manifest, defines, sources, compiler.Identity);
        string tree = Path.Combine(cacheRoot, "shaderpacks", manifest.Id + "-" + hash[..16]);
        string treeError;
        try
        {
            Materialize(tree, sources, defines, out treeError);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            reason = error.Message;
            return null;
        }
        if (treeError.Length > 0)
        {
            reason = treeError;
            return null;
        }

        var builder = new NativeShaderBuilder(compiler);
        var merged = new NativeShaderBuildResult { Manifest = { Toolchain = compiler.Identity } };
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach ((string slot, _) in sources)
            {
                NativeShaderBuildResult one = builder.Build(tree, slot);
                if (one.Manifest.Programs.Count == 0)
                {
                    failures[slot] = one.Errors.Count > 0 ? Shorten(one.Errors) : "no variant compiled";
                    continue;
                }
                merged.Manifest.CapacityTier = one.Manifest.CapacityTier;
                merged.Manifest.Programs.AddRange(one.Manifest.Programs);
                foreach (KeyValuePair<string, byte[]> file in one.Files) merged.Files[file.Key] = file.Value;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            reason = "build of " + tree + " failed: " + error.Message;
            return null;
        }

        if (merged.Manifest.Programs.Count == 0)
        {
            reason = "no program built; " + Shorten(new List<string>(failures.Values));
            return null;
        }

        reason = "";
        return new ShaderPackLibrary(manifest, NativeShaderLibrary.FromBuild(merged), packDirectory, hash, failures);
    }

    /// <summary>Deletes any previous tree and writes the program sources and the embedded include set.</summary>
    private static void Materialize(string tree, List<(string Slot, string Text)> sources,
        IReadOnlyDictionary<string, string> defines, out string error)
    {
        error = "";
        if (Directory.Exists(tree)) Directory.Delete(tree, true);
        Directory.CreateDirectory(Path.Combine(tree, NativeShaderBuilder.IncludeDirectoryName));

        string prefix = DefinesPrefix(defines);
        foreach ((string slot, string text) in sources)
        {
            File.WriteAllText(Path.Combine(tree, slot + ".glsl"), ShaderCompiler.SplicePrefix(text, prefix));
        }

        int includes = WriteIncludes(Path.Combine(tree, NativeShaderBuilder.IncludeDirectoryName));
        if (includes == 0)
        {
            error = "the runtime has no embedded " + IncludeResourcePrefix + "* include files";
        }
    }

    private static string DefinesPrefix(IReadOnlyDictionary<string, string> defines)
    {
        var text = new StringBuilder();
        foreach (KeyValuePair<string, string> define in defines)
        {
            text.Append("#define ").Append(define.Key).Append(' ').Append(define.Value).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>Writes the runtime's embedded native includes; the count written.</summary>
    private static int WriteIncludes(string includeDirectory)
    {
        System.Reflection.Assembly assembly = typeof(ShaderPackLibrary).Assembly;
        int written = 0;
        foreach (string name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(IncludeResourcePrefix, StringComparison.Ordinal)) continue;
            using Stream? stream = assembly.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            File.WriteAllText(Path.Combine(includeDirectory, name[IncludeResourcePrefix.Length..]), reader.ReadToEnd());
            written++;
        }
        return written;
    }

    private static string ContentHashOf(ShaderPackManifest manifest, IReadOnlyDictionary<string, string> defines,
        List<(string Slot, string Text)> sources, string compilerIdentity)
    {
        var text = new StringBuilder();
        text.Append("contract=").Append(manifest.Contract).Append('\n');
        text.Append("id=").Append(manifest.Id).Append('\n');
        text.Append("compiler=").Append(compilerIdentity).Append('\n');
        foreach (KeyValuePair<string, string> define in defines)
        {
            text.Append("define ").Append(define.Key).Append(' ').Append(define.Value).Append('\n');
        }
        sources.Sort((a, b) => string.CompareOrdinal(a.Slot, b.Slot));
        foreach ((string slot, string source) in sources)
        {
            text.Append("slot ").Append(slot).Append('\n').Append(source).Append('\n');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    private static string Shorten(List<string> errors)
    {
        if (errors.Count == 0) return "unknown build error";
        return errors[0].Length > 300 ? errors[0][..300] + "..." : errors[0];
    }
}
