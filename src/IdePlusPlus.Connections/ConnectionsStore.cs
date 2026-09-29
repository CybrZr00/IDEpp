using System.Text.Json;

namespace IdePlusPlus.Connections;

/// <summary>Id is the key SecureSettingsStore holds this connection's actual connection string under
/// ("Connection:{Id}") - nothing sensitive lives in connections.json itself, the same split already
/// used for LLM provider API keys.</summary>
public sealed record StoredConnection(string Id, string Name);

/// <summary>Persists the set of manually-added connections at .ideplusplus/connections.json, next to
/// the solution - same per-solution JSON convention as StartupConfigurationStore/
/// McpServerConfigStore. Discovered (as opposed to user-added) connections are never persisted here:
/// they're re-scanned live on every solution load, so this file can never go stale relative to the
/// config files it would otherwise be duplicating.</summary>
public static class ConnectionsStore
{
    private static readonly JsonSerializerOptions SerializerOptions =
        new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private static string FilePath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "connections.json");

    public static IReadOnlyList<StoredConnection> Load(string solutionDirectory)
    {
        var path = FilePath(solutionDirectory);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<StoredConnection>>(File.ReadAllText(path), SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static void Save(string solutionDirectory, IReadOnlyList<StoredConnection> connections)
    {
        var path = FilePath(solutionDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(connections, SerializerOptions));
    }
}
