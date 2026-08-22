using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Mcp;

/// <summary>
/// A tool discovered from an MCP server. QualifiedName is what's actually exposed to the chat
/// model (server-prefixed, e.g. "playwright__browser_navigate") so tools from different servers
/// can never collide - the same convention Claude Code itself uses for MCP tool names.
/// </summary>
public sealed record McpToolInfo(string ServerName, string Name, string? Description, JsonNode InputSchema)
{
    public string QualifiedName => $"{ServerName}__{Name}";
}

public sealed record McpToolCallResult(bool IsError, string Text);
