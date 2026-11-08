namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// A snapshot of one MCP server's health, for the MCP Servers pane and the Settings dialog. Includes
/// the last tool call's time and duration, and a tail of the server's recent stderr - the output that
/// used to go nowhere a user could see when a server misbehaved.
/// </summary>
public sealed record McpServerStatus(
    string Name,
    bool IsConnected,
    string? Error,
    int ToolCount,
    DateTimeOffset? LastCallAt,
    double? LastDurationMs,
    IReadOnlyList<string> RecentStderr);
