using System.Text.Json;
using System.Text.Json.Serialization;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// Persists the configured MCP server list, by default at %LOCALAPPDATA%\IdePlusPlus\mcp-servers.json
/// - a user-level tool configuration (like which extensions are installed), not a per-solution
/// setting, so unlike project memory/embeddings this isn't scoped under a solution directory.
/// </summary>
public sealed class McpServerConfigStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "mcp-servers.json");

    private readonly string _filePath;

    public McpServerConfigStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    public IReadOnlyList<McpServerConfig> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var records = JsonSerializer.Deserialize<List<StoredServer>>(File.ReadAllText(_filePath));
            return records?.Select(r => new McpServerConfig(r.Name, r.Command, r.Args, r.Env)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public void Save(IReadOnlyList<McpServerConfig> servers)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var records = servers.Select(s => new StoredServer(s.Name, s.Command, s.Args.ToList(), s.Env)).ToList();
        File.WriteAllText(_filePath, JsonSerializer.Serialize(records, SerializerOptions));
    }

    private sealed record StoredServer(
        string Name,
        string Command,
        [property: JsonPropertyName("args")] List<string> Args,
        [property: JsonPropertyName("env")] IReadOnlyDictionary<string, string>? Env);
}
