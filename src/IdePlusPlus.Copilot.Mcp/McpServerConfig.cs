namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// A configured MCP server, launched as a local subprocess over the stdio transport (the only
/// transport this client supports - see McpClient). Env is for servers that need an API key
/// (e.g. some hosted-lookup MCP servers) without making the user set a machine-wide variable.
/// </summary>
public sealed record McpServerConfig(
    string Name,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string>? Env = null);
