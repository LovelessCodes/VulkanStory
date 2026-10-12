using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VulkanStory.Render.Vulkan.Shaders;

/// <summary>
/// <c>pack.json</c>: the manifest of one shader pack directory (docs/shader-packs.md, contract 1).
///
/// A pack replaces named programs of the shipped native library with GLSL 450 sources
/// compiled at device start, and declares user options that become <c>#define</c>s in
/// those sources. It carries no compiled artifacts and no code: everything a pack can
/// do goes through the native shader contract.
///
/// Parsed with <see cref="JsonDocument" /> directly, with every unknown field refused
/// rather than ignored, so a pack written against another contract revision fails loudly.
/// </summary>
internal sealed class ShaderPackManifest
{
    /// <summary>Bumped whenever a field is added, removed or changes meaning; loaders refuse any other value.</summary>
    public const int CurrentContract = 1;

    public const string FileName = "pack.json";

    /// <summary>Cap on the manifest text; a pack's options and program list are a few KiB.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <summary>Pack ids are used in cache paths, so path metacharacters are out.</summary>
    private static readonly Regex PackIdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>Option ids become GLSL macros.</summary>
    private static readonly Regex OptionIdPattern = new(@"^[A-Z][A-Z0-9_]{0,31}$", RegexOptions.Compiled);

    /// <summary>Slot names are the shipped programs' <c>PassName</c>s.</summary>
    private static readonly Regex SlotPattern = new(@"^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.Compiled);

    public int Contract = CurrentContract;
    public string Id = "";
    public string Name = "";
    public string Version = "";
    public List<ShaderPackOption> Options = new();
    public List<ShaderPackProgram> Programs = new();

    // ------------------------------------------------------------------ parsing

    /// <summary>
    /// Parses a manifest. Throws <see cref="InvalidDataException" /> for malformed JSON, a
    /// missing or unknown field, another contract revision, or an invalid id or path.
    /// </summary>
    public static ShaderPackManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("shader pack manifest is not valid JSON: " + error.Message, error);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("shader pack manifest: the root must be an object");
            }

            var manifest = new ShaderPackManifest();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            bool hasContract = false, hasId = false, hasName = false;
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    throw new InvalidDataException("shader pack manifest: duplicate field '" + property.Name + "'");
                }
                switch (property.Name)
                {
                    case "contract":
                        manifest.Contract = RequireInt(property.Value, "contract");
                        hasContract = true;
                        break;
                    case "id":
                        manifest.Id = RequireString(property.Value, "id");
                        hasId = true;
                        break;
                    case "name":
                        manifest.Name = RequireString(property.Value, "name");
                        hasName = true;
                        break;
                    case "version":
                        manifest.Version = RequireString(property.Value, "version");
                        break;
                    case "options":
                        foreach (JsonElement option in RequireArray(property.Value, "options"))
                        {
                            manifest.Options.Add(ReadOption(option, manifest));
                        }
                        break;
                    case "programs":
                        foreach (JsonElement program in RequireArray(property.Value, "programs"))
                        {
                            manifest.Programs.Add(ReadProgram(program, manifest));
                        }
                        break;
                    default:
                        throw new InvalidDataException("shader pack manifest: unknown field '" + property.Name + "'");
                }
            }

            if (!hasContract) throw new InvalidDataException("shader pack manifest: missing 'contract'");
            if (manifest.Contract != CurrentContract)
            {
                throw new InvalidDataException(
                    "shader pack manifest: contract " + manifest.Contract + ", this runtime implements " + CurrentContract);
            }
            if (!hasId) throw new InvalidDataException("shader pack manifest: missing 'id'");
            if (!PackIdPattern.IsMatch(manifest.Id))
            {
                throw new InvalidDataException("shader pack manifest: id '" + manifest.Id + "' is not [A-Za-z0-9][A-Za-z0-9._-]{0,63}");
            }
            if (!hasName) throw new InvalidDataException("shader pack manifest: missing 'name'");
            if (manifest.Name.Length is 0 or > 128)
            {
                throw new InvalidDataException("shader pack manifest: name must be 1-128 characters");
            }
            if (manifest.Version.Length > 64)
            {
                throw new InvalidDataException("shader pack manifest: version is longer than 64 characters");
            }
            if (manifest.Programs.Count == 0)
            {
                throw new InvalidDataException("shader pack manifest: 'programs' is empty; a pack must replace at least one program");
            }
            return manifest;
        }
    }

    private static ShaderPackProgram ReadProgram(JsonElement element, ShaderPackManifest manifest)
    {
        var program = new ShaderPackProgram();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in Object(element, "program"))
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("shader pack manifest: duplicate program field '" + property.Name + "'");
            switch (property.Name)
            {
                case "slot":
                    program.Slot = RequireString(property.Value, "slot");
                    break;
                case "source":
                    program.Source = RequireString(property.Value, "source");
                    break;
                default:
                    throw new InvalidDataException("shader pack manifest: unknown program field '" + property.Name + "'");
            }
        }
        if (!SlotPattern.IsMatch(program.Slot))
        {
            throw new InvalidDataException("shader pack manifest: slot '" + program.Slot + "' is not a program name");
        }
        if (manifest.Programs.Exists(p => p.Slot == program.Slot))
        {
            throw new InvalidDataException("shader pack manifest: slot '" + program.Slot + "' is declared twice");
        }
        if (!IsSafeRelativePath(program.Source))
        {
            throw new InvalidDataException("shader pack manifest: source '" + program.Source + "' is not a relative .glsl path inside the pack");
        }
        return program;
    }

    private static ShaderPackOption ReadOption(JsonElement element, ShaderPackManifest manifest)
    {
        var option = new ShaderPackOption();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool hasDefault = false;
        JsonElement defaultElement = default;
        foreach (JsonProperty property in Object(element, "option"))
        {
            if (!seen.Add(property.Name)) throw new InvalidDataException("shader pack manifest: duplicate option field '" + property.Name + "'");
            switch (property.Name)
            {
                case "id":
                    option.Id = RequireString(property.Value, "id");
                    break;
                case "type":
                    option.Type = RequireString(property.Value, "type");
                    break;
                case "values":
                    foreach (JsonElement value in RequireArray(property.Value, "values"))
                    {
                        option.Values.Add(RequireString(value, "values element"));
                    }
                    break;
                case "default":
                    defaultElement = property.Value;
                    hasDefault = true;
                    break;
                default:
                    throw new InvalidDataException("shader pack manifest: unknown option field '" + property.Name + "'");
            }
        }
        if (!OptionIdPattern.IsMatch(option.Id))
        {
            throw new InvalidDataException("shader pack manifest: option id '" + option.Id + "' is not an uppercase macro name");
        }
        if (manifest.Options.Exists(o => o.Id == option.Id))
        {
            throw new InvalidDataException("shader pack manifest: option '" + option.Id + "' is declared twice");
        }
        if (!hasDefault) throw new InvalidDataException("shader pack manifest: option '" + option.Id + "' has no default");
        switch (option.Type)
        {
            case "bool":
                if (defaultElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new InvalidDataException("shader pack manifest: bool option '" + option.Id + "' needs a true or false default");
                }
                option.Default = defaultElement.GetBoolean() ? "1" : "0";
                break;
            case "choice":
                if (option.Values.Count < 2 || option.Values.Count > 16)
                {
                    throw new InvalidDataException("shader pack manifest: choice option '" + option.Id + "' needs 2-16 values");
                }
                foreach (string value in option.Values)
                {
                    if (value.Length is 0 or > 64)
                    {
                        throw new InvalidDataException("shader pack manifest: choice option '" + option.Id + "' has an empty or overlong value");
                    }
                }
                if (new HashSet<string>(option.Values, StringComparer.Ordinal).Count != option.Values.Count)
                {
                    throw new InvalidDataException("shader pack manifest: choice option '" + option.Id + "' repeats a value");
                }
                option.Default = RequireString(defaultElement, "default");
                if (!option.Values.Contains(option.Default))
                {
                    throw new InvalidDataException("shader pack manifest: choice option '" + option.Id + "' default '" + option.Default + "' is not one of its values");
                }
                break;
            default:
                throw new InvalidDataException("shader pack manifest: option '" + option.Id + "' has type '" + option.Type + "', contract 1 has bool and choice");
        }
        return option;
    }

    /// <summary>A path under the pack directory: forward or back slashes, no absolute form, no <c>..</c>, no empty segment.</summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length is 0 or > 200 || path[0] is '/' or '\\' || path.IndexOf(':') >= 0) return false;
        foreach (string segment in path.Split('/', '\\'))
        {
            if (segment.Length == 0 || segment is "." or "..") return false;
            foreach (char c in segment)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.')) return false;
            }
        }
        return true;
    }

    // ------------------------------------------------------------------ options

    /// <summary>The option defines at their defaults; one line per option, ready to splice into a source.</summary>
    public Dictionary<string, string> ResolveDefines() => ResolveDefines(null);

    /// <summary>
    /// The option defines for a set of user selections (null or a missing id means its default):
    /// a bool is <c>0</c> or <c>1</c>, a choice is the value's index. Throws
    /// <see cref="InvalidDataException" /> for a selection that is not one of the option's values.
    /// </summary>
    public Dictionary<string, string> ResolveDefines(IReadOnlyDictionary<string, string>? selected)
    {
        var defines = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (ShaderPackOption option in Options)
        {
            string value = option.Default;
            if (selected != null && selected.TryGetValue(option.Id, out string? chosen) && chosen != null) value = chosen;
            defines[option.Id] = option.Type switch
            {
                "bool" => Truthy(value, option.Id),
                "choice" => ChoiceIndex(option, value),
                _ => throw new InvalidDataException("shader pack option '" + option.Id + "' has type '" + option.Type + "'"),
            };
        }
        return new Dictionary<string, string>(defines, StringComparer.Ordinal);
    }

    private static string Truthy(string value, string option)
    {
        switch (value.Trim().ToLowerInvariant())
        {
            case "1" or "true" or "on" or "yes": return "1";
            case "0" or "false" or "off" or "no": return "0";
            default:
                throw new InvalidDataException("shader pack option '" + option + "' cannot read '" + value + "' as a bool");
        }
    }

    private static string ChoiceIndex(ShaderPackOption option, string value)
    {
        int index = option.Values.IndexOf(value);
        if (index < 0)
        {
            throw new InvalidDataException("shader pack option '" + option.Id + "' cannot read '" + value + "'; values are " + string.Join(", ", option.Values));
        }
        return index.ToString(CultureInfo.InvariantCulture);
    }

    // ------------------------------------------------------------------ JSON helpers

    private static IEnumerable<JsonProperty> Object(JsonElement element, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("shader pack manifest: " + what + " must be an object");
        }
        return element.EnumerateObject();
    }

    private static JsonElement.ArrayEnumerator RequireArray(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("shader pack manifest: '" + field + "' must be an array");
        }
        return element.EnumerateArray();
    }

    private static string RequireString(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("shader pack manifest: '" + field + "' must be a string");
        }
        return element.GetString()!;
    }

    private static int RequireInt(JsonElement element, string field)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int value))
        {
            throw new InvalidDataException("shader pack manifest: '" + field + "' must be an integer");
        }
        return value;
    }
}

/// <summary>One replaceable program: the shipped program's name and the pack-relative source that replaces it.</summary>
internal sealed class ShaderPackProgram
{
    /// <summary>The shipped program's <c>PassName</c>, which is also its manifest entry name.</summary>
    public string Slot = "";

    /// <summary>Pack-relative path of the GLSL 450 source; one file carries both stages as the native corpus does.</summary>
    public string Source = "";
}

/// <summary>One user option; its id is the GLSL macro the resolved value defines.</summary>
internal sealed class ShaderPackOption
{
    public string Id = "";

    /// <summary><c>bool</c> or <c>choice</c> in contract 1.</summary>
    public string Type = "bool";

    /// <summary>Choice values in declaration order; empty for a bool.</summary>
    public List<string> Values = new();

    /// <summary>Canonical default: <c>0</c>/<c>1</c> for a bool, the value string for a choice.</summary>
    public string Default = "0";
}
