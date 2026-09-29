using System.Xml.Linq;

namespace IdePlusPlus.Publish;

/// <summary>
/// Parses a .pubxml into a <see cref="PublishProfile"/>.
///
/// Defensive in the same way TrxParser is: a file that isn't a publish profile, or is malformed XML,
/// comes back null rather than throwing. A single bad file in Properties/PublishProfiles must not
/// take the whole Publish dialog down with it.
///
/// Every lookup is by <see cref="XName.LocalName"/> rather than a fully-qualified name. Publish
/// profiles come in two flavours: legacy Visual Studio ones declare
/// xmlns="http://schemas.microsoft.com/developer/msbuild/2003" on the root, SDK-style ones declare
/// no namespace at all. Matching on the local name reads both without caring which it got.
/// </summary>
public static class PublishProfileReader
{
    /// <summary>Written by IDE++ to record that a profile targets a container image. There is no
    /// Visual Studio convention for this - .pubxml has never had a container protocol - so IDE++
    /// marks it with a property of its own. MSBuild ignores properties nothing reads, so the file
    /// stays a genuine .pubxml that `dotnet publish -p:PublishProfile=x` still imports correctly.</summary>
    public const string KindMarkerProperty = "IdePlusPlusPublishKind";

    /// <summary>Properties the reader turns into typed fields. Everything else lands in
    /// <see cref="PublishProfile.AdditionalProperties"/> untouched.</summary>
    private static readonly HashSet<string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "PublishProtocol",
        KindMarkerProperty,
        "Configuration",
        "TargetFramework",
        "RuntimeIdentifier",
        "SelfContained",
        "PublishDir",
        "PublishSingleFile",
        "PublishReadyToRun",
        "PublishTrimmed",
        "ContainerRepository",
        "ContainerImageTag",
        "ContainerRegistry",
        "ContainerBaseImage",
    };

    public static PublishProfile? ParseFile(string pubxmlPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(pubxmlPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Parse(text, Path.GetFileNameWithoutExtension(pubxmlPath), pubxmlPath);
    }

    public static PublishProfile? Parse(string pubxml, string name, string? filePath = null)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(pubxml);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        if (document.Root is not { } root || !string.Equals(root.Name.LocalName, "Project", StringComparison.Ordinal))
        {
            return null;
        }

        // Last one wins, matching MSBuild: later PropertyGroups override earlier ones. Profiles are
        // almost always a single PropertyGroup, but nothing stops them from not being.
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var propertyGroup in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
        {
            foreach (var property in propertyGroup.Elements())
            {
                properties[property.Name.LocalName] = property.Value.Trim();
            }
        }

        var additional = properties
            .Where(p => !KnownProperties.Contains(p.Key))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

        return new PublishProfile
        {
            Name = name,
            FilePath = filePath,
            Kind = DetermineKind(properties),
            Configuration = Value(properties, "Configuration") ?? "Release",
            TargetFramework = Value(properties, "TargetFramework"),
            RuntimeIdentifier = Value(properties, "RuntimeIdentifier"),
            SelfContained = NullableBool(properties, "SelfContained"),
            PublishDir = Value(properties, "PublishDir"),
            PublishSingleFile = Bool(properties, "PublishSingleFile"),
            PublishReadyToRun = Bool(properties, "PublishReadyToRun"),
            PublishTrimmed = Bool(properties, "PublishTrimmed"),
            ContainerRepository = Value(properties, "ContainerRepository"),
            ContainerImageTag = Value(properties, "ContainerImageTag"),
            ContainerRegistry = Value(properties, "ContainerRegistry"),
            ContainerBaseImage = Value(properties, "ContainerBaseImage"),
            AdditionalProperties = additional,
        };
    }

    /// <summary>
    /// A profile with no protocol at all is a Folder publish - that's what the SDK does with a bare
    /// PropertyGroup, and it's what the hand-written profiles in this very repo look like. Anything
    /// naming a protocol IDE++ can't drive (MSDeploy, ClickOnce, AzureWebSite, FTP) is Unsupported
    /// rather than rejected, so it still shows up in the list as something that exists.
    ///
    /// WebPublishMethod is the legacy alias for PublishProtocol and is only read here - it stays in
    /// AdditionalProperties so an old web profile round-trips with it intact.
    /// </summary>
    private static PublishKind DetermineKind(IReadOnlyDictionary<string, string> properties)
    {
        if (string.Equals(Value(properties, KindMarkerProperty), "Container", StringComparison.OrdinalIgnoreCase))
        {
            return PublishKind.Container;
        }

        var protocol = Value(properties, "PublishProtocol") ?? Value(properties, "WebPublishMethod");
        return protocol is null
               || protocol.Equals("FileSystem", StringComparison.OrdinalIgnoreCase)
               || protocol.Equals("Folder", StringComparison.OrdinalIgnoreCase)
            ? PublishKind.Folder
            : PublishKind.Unsupported;
    }

    private static string? Value(IReadOnlyDictionary<string, string> properties, string name) =>
        properties.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    private static bool Bool(IReadOnlyDictionary<string, string> properties, string name) =>
        NullableBool(properties, name) ?? false;

    private static bool? NullableBool(IReadOnlyDictionary<string, string> properties, string name) =>
        Value(properties, name) is { } raw && bool.TryParse(raw, out var parsed) ? parsed : null;
}
