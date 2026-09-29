using System.Text.Json;

namespace IdePlusPlus.Connections;

/// <summary>Reads the "ConnectionStrings" section of an appsettings*.json file - the standard
/// ASP.NET Core convention. Same defensive-parsing idiom as IdePlusPlus.Run's LaunchSettingsLoader:
/// a missing or malformed file yields no results rather than throwing.</summary>
public static class AppSettingsConnectionReader
{
    public static IReadOnlyList<DiscoveredConnection> Read(string filePath, string? projectPath)
    {
        if (!File.Exists(filePath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!document.RootElement.TryGetProperty("ConnectionStrings", out var connectionStrings)
                || connectionStrings.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var results = new List<DiscoveredConnection>();
            foreach (var entry in connectionStrings.EnumerateObject())
            {
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString() is { Length: > 0 } value)
                {
                    results.Add(new DiscoveredConnection(
                        entry.Name, value, filePath, projectPath, ConnectionSourceKind.AppSettingsJson));
                }
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
