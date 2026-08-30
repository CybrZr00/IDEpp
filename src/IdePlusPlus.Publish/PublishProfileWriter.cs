using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace IdePlusPlus.Publish;

/// <summary>
/// Turns a <see cref="PublishProfile"/> back into .pubxml text.
///
/// When the profile came from a file that still exists, the existing document is edited in place
/// rather than regenerated: elements are located by local name and their values replaced, and
/// anything the reader didn't model is left exactly where it was. That's what keeps comments, element
/// order, the original XML namespace, and every Visual-Studio-authored property intact through a
/// save. Regenerating from the typed model alone would quietly delete all of it - including the
/// explanatory comment block in this repo's own win-x64.pubxml.
/// </summary>
public static class PublishProfileWriter
{
    public static string ToXml(PublishProfile profile, string? existingXml = null)
    {
        if (profile.Kind == PublishKind.Unsupported)
        {
            throw new ArgumentException(
                $"Publish profile '{profile.Name}' uses a protocol IDE++ can't run, so it is read-only. " +
                "Writing it would discard the settings for that protocol.",
                nameof(profile));
        }

        var desired = BuildProperties(profile);
        var document = TryParse(existingXml) ?? NewDocument();
        UpdateDocument(document, desired);
        return Serialize(document);
    }

    /// <summary>
    /// The properties a save should end up with. Ordered, because a freshly created file gets them in
    /// this order; an in-place edit keeps whatever order the file already had.
    ///
    /// PublishProtocol is written for containers too. A container publish still runs the ordinary
    /// publish pipeline into a folder before packing an image, and leaving the protocol out would
    /// make the file look like a profile of no particular kind to Visual Studio.
    /// </summary>
    private static List<KeyValuePair<string, string?>> BuildProperties(PublishProfile profile)
    {
        var isContainer = profile.Kind == PublishKind.Container;

        // A null value means "this property should not be in the file" - UpdateDocument removes it.
        var properties = new List<KeyValuePair<string, string?>>
        {
            new("PublishProtocol", "FileSystem"),
            new(PublishProfileReader.KindMarkerProperty, isContainer ? "Container" : null),
            new("Configuration", profile.Configuration),
            new("TargetFramework", Trimmed(profile.TargetFramework)),
            new("PublishDir", Trimmed(profile.PublishDir)),
            new("RuntimeIdentifier", Trimmed(profile.RuntimeIdentifier)),
            new("SelfContained", profile.SelfContained is { } selfContained ? Bool(selfContained) : null),
            new("PublishSingleFile", isContainer ? null : Bool(profile.PublishSingleFile)),
            new("PublishReadyToRun", isContainer ? null : Bool(profile.PublishReadyToRun)),
            new("PublishTrimmed", isContainer ? null : Bool(profile.PublishTrimmed)),
            new("ContainerRepository", isContainer ? Trimmed(profile.ContainerRepository) : null),
            new("ContainerImageTag", isContainer ? Trimmed(profile.ContainerImageTag) : null),
            new("ContainerRegistry", isContainer ? Trimmed(profile.ContainerRegistry) : null),
            new("ContainerBaseImage", isContainer ? Trimmed(profile.ContainerBaseImage) : null),
        };

        foreach (var additional in profile.AdditionalProperties.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            properties.Add(new KeyValuePair<string, string?>(additional.Key, additional.Value));
        }

        return properties;
    }

    private static void UpdateDocument(XDocument document, List<KeyValuePair<string, string?>> desired)
    {
        var root = document.Root!;
        var propertyGroups = root.Elements().Where(e => e.Name.LocalName == "PropertyGroup").ToList();

        foreach (var (name, value) in desired)
        {
            var existing = propertyGroups
                .SelectMany(g => g.Elements())
                .Where(e => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (value is null)
            {
                existing.ForEach(e => e.Remove());
                continue;
            }

            if (existing.Count > 0)
            {
                // Last one wins at evaluation time, so that's the one to update; any earlier
                // duplicates are dead weight the reader already ignored.
                existing[^1].Value = value;
                continue;
            }

            var target = propertyGroups.FirstOrDefault() ?? AddPropertyGroup(root);
            propertyGroups = [target, .. propertyGroups.Where(g => g != target)];
            target.Add(new XElement(target.Name.Namespace + name, value));
        }
    }

    private static XElement AddPropertyGroup(XElement root)
    {
        var propertyGroup = new XElement(root.Name.Namespace + "PropertyGroup");
        root.Add(propertyGroup);
        return propertyGroup;
    }

    private static XDocument NewDocument() => new(new XElement("Project", new XElement("PropertyGroup")));

    private static XDocument? TryParse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return null;
        }

        try
        {
            var document = XDocument.Parse(xml);
            return document.Root?.Name.LocalName == "Project" ? document : null;
        }
        catch (XmlException)
        {
            // Unparseable existing file - fall back to generating a fresh one rather than refusing to
            // save. There is nothing in it worth preserving if MSBuild can't read it either.
            return null;
        }
    }

    private static string Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            // Profiles written by hand (and by the SDK templates) have no declaration; ones written by
            // Visual Studio do. Keep whichever the file already used.
            OmitXmlDeclaration = document.Declaration is null,
        };

        var output = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(output, settings))
        {
            document.Save(writer);
        }

        return output.ToString();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>StringWriter reports UTF-16 as its encoding, which would put encoding="utf-16" into
    /// the declaration of a file that is then written to disk as UTF-8.</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
