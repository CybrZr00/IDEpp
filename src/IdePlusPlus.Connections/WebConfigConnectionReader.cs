using System.Xml;
using System.Xml.Linq;

namespace IdePlusPlus.Connections;

/// <summary>Reads the &lt;connectionStrings&gt; section of a web.config/app.config file - the
/// classic .NET Framework convention, still common in a solution with a mix of old and new
/// projects. Same defensive-parsing posture as AppSettingsConnectionReader: a missing or malformed
/// file yields no results rather than throwing.</summary>
public static class WebConfigConnectionReader
{
    public static IReadOnlyList<DiscoveredConnection> Read(string filePath, string? projectPath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            var document = XDocument.Load(filePath);
            var kind = Path.GetFileName(filePath).Equals("web.config", StringComparison.OrdinalIgnoreCase)
                ? ConnectionSourceKind.WebConfig
                : ConnectionSourceKind.AppConfig;

            var results = new List<DiscoveredConnection>();
            foreach (var add in document.Descendants("connectionStrings").Elements("add"))
            {
                var name = add.Attribute("name")?.Value;
                var connectionString = add.Attribute("connectionString")?.Value;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(connectionString))
                {
                    results.Add(new DiscoveredConnection(name, connectionString, filePath, projectPath, kind));
                }
            }

            return results;
        }
        catch (Exception ex) when (ex is XmlException or IOException)
        {
            return [];
        }
    }
}
