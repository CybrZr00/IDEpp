using System.Text.Json;

namespace IdePlusPlus.Templates;

/// <summary>
/// Reads the .NET SDK's own template cache - <c>templatecache.json</c>, ~1.2 MB and 228 templates on
/// a stock .NET 10 install.
///
/// <para>This exists because <c>dotnet new list</c> cannot be parsed: it prints a fixed-width table
/// that truncates the template-name column (three separate ASP.NET Core templates all render as
/// "ASP.NET Core We...") and its headers are localized. The cache carries the untruncated name, the
/// short names, description, author, classifications, precedence and the full parameter list, and
/// reading it costs a millisecond against a second or more per process spawn.</para>
///
/// <para>The trade is that this format belongs to the template engine and is not a documented
/// contract - it carries its own version, checked below. Every failure path (missing file, malformed
/// JSON, unexpected version, an entry without an identity) degrades to nothing rather than throwing,
/// the same posture as <c>PackageJsonReader</c>: no template list is a recoverable state, an
/// exception out of a menu-opening path is not.</para>
/// </summary>
public static class TemplateCacheReader
{
    /// <summary>The cache format this reader was written against. Only the major version is enforced -
    /// the engine bumps the trailing components for additive changes, and refusing to read a cache
    /// over a field this doesn't use would disable templates for no reason.</summary>
    public const int SupportedMajorVersion = 1;

    /// <summary>Null when the cache is unusable - unparseable, not an object, or a version this
    /// reader doesn't understand. An empty list means it was read and genuinely holds no templates,
    /// which is a different thing and worth a different message.</summary>
    public static IReadOnlyList<TemplateInfo>? Read(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !IsSupportedVersion(root))
            {
                return null;
            }

            if (!root.TryGetProperty("TemplateInfo", out var templates) || templates.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var results = new List<TemplateInfo>();
            foreach (var element in templates.EnumerateArray())
            {
                if (ReadTemplate(element) is { } template)
                {
                    results.Add(template);
                }
            }

            return results;
        }
    }

    /// <summary>Reads a cache file from disk, returning null for anything unreadable so a missing or
    /// locked cache behaves the same as an unparseable one.</summary>
    public static IReadOnlyList<TemplateInfo>? ReadFile(string path)
    {
        try
        {
            return Read(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsSupportedVersion(JsonElement root)
    {
        // A cache with no version at all predates the field entirely; refuse it rather than guessing
        // at a shape this was never checked against.
        if (!root.TryGetProperty("Version", out var version) || version.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = version.GetString();
        var firstSeparator = text?.IndexOf('.') ?? -1;
        var major = firstSeparator > 0 ? text![..firstSeparator] : text;

        return int.TryParse(major, out var parsed) && parsed == SupportedMajorVersion;
    }

    private static TemplateInfo? ReadTemplate(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        // Identity is what makes an entry addressable at all; name is what the user picks from.
        // Without either there is nothing worth listing.
        var identity = ReadString(element, "Identity");
        var name = ReadString(element, "Name");
        if (identity is null || name is null)
        {
            return null;
        }

        var tags = element.TryGetProperty("TagsCollection", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Object
            ? tagsElement
            : default;

        // One entry in the installed cache has no group identity. Falling back to the identity makes
        // it its own group of one rather than lumping every such template together under "".
        var groupIdentity = ReadString(element, "GroupIdentity") ?? identity;

        return new TemplateInfo(
            identity,
            groupIdentity,
            ReadStringArray(element, "ShortNameList"),
            name,
            ReadString(element, "Description"),
            ReadString(element, "Author"),
            ReadStringArray(element, "Classifications"),
            tags.ValueKind == JsonValueKind.Object ? ReadString(tags, "language") : null,
            ParseKind(tags.ValueKind == JsonValueKind.Object ? ReadString(tags, "type") : null),
            ReadInt(element, "Precedence"),
            ReadString(element, "DefaultName"),
            ReadParameters(element));
    }

    private static TemplateKind ParseKind(string? type) => type?.ToLowerInvariant() switch
    {
        "project" => TemplateKind.Project,
        "item" => TemplateKind.Item,
        "solution" => TemplateKind.Solution,
        _ => TemplateKind.Unknown,
    };

    private static IReadOnlyList<TemplateParameter> ReadParameters(JsonElement element)
    {
        if (!element.TryGetProperty("Parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<TemplateParameter>();
        foreach (var parameter in parameters.EnumerateArray())
        {
            if (parameter.ValueKind != JsonValueKind.Object || ReadString(parameter, "Name") is not { } name)
            {
                continue;
            }

            results.Add(new TemplateParameter(
                name,
                ReadString(parameter, "DisplayName"),
                ReadString(parameter, "Description") ?? ReadString(parameter, "Documentation"),
                ParseDataType(ReadString(parameter, "DataType")),
                ReadChoices(parameter),
                ReadString(parameter, "DefaultValue"),
                ReadIsRequired(parameter),
                parameter.TryGetProperty("IsName", out var isName) && isName.ValueKind == JsonValueKind.True));
        }

        return results;
    }

    private static TemplateParameterDataType ParseDataType(string? dataType) => dataType?.ToLowerInvariant() switch
    {
        "string" => TemplateParameterDataType.String,
        "text" => TemplateParameterDataType.Text,
        "bool" => TemplateParameterDataType.Bool,
        "integer" or "int" => TemplateParameterDataType.Integer,
        "choice" => TemplateParameterDataType.Choice,
        _ => TemplateParameterDataType.Unknown,
    };

    /// <summary>Choices are an object keyed by the value that gets passed on the command line, with
    /// display metadata inside - not an array.</summary>
    private static IReadOnlyList<TemplateParameterChoice> ReadChoices(JsonElement parameter)
    {
        if (!parameter.TryGetProperty("Choices", out var choices) || choices.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        var results = new List<TemplateParameterChoice>();
        foreach (var choice in choices.EnumerateObject())
        {
            var isObject = choice.Value.ValueKind == JsonValueKind.Object;
            results.Add(new TemplateParameterChoice(
                choice.Name,
                isObject ? ReadString(choice.Value, "DisplayName") : null,
                isObject ? ReadString(choice.Value, "Description") : null));
        }

        return results;
    }

    private static bool ReadIsRequired(JsonElement parameter) =>
        parameter.TryGetProperty("Precedence", out var precedence) &&
        precedence.ValueKind == JsonValueKind.Object &&
        precedence.TryGetProperty("IsRequired", out var isRequired) &&
        isRequired.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int ReadInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var parsed)
            ? parsed
            : 0;

    private static IReadOnlyList<string> ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
            {
                results.Add(text);
            }
        }

        return results;
    }
}
