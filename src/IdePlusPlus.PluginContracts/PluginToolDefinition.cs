using System.Text.Json.Nodes;

namespace IdePlusPlus.PluginContracts;

/// <summary>
/// A chat tool a plugin contributes via <see cref="IPluginHost.RegisterChatTool"/>. Shape
/// deliberately mirrors <c>IdePlusPlus.Copilot.Core.ToolDefinition</c> (and MCP's
/// <c>McpToolInfo</c>) so the same JSON-schema-driven tool-calling convention the chat assistant
/// already speaks extends to plugins without a second dialect. IDE++ registers each one with the
/// assistant under the qualified name <c>"plugin__{PluginName}__{Name}"</c>, mirroring MCP's
/// <c>"{ServerName}__{Name}"</c> convention, so tools from different plugins (or a plugin and an MCP
/// server) never collide even if they pick the same <see cref="Name"/>.
/// </summary>
public sealed record PluginToolDefinition(string Name, string Description, JsonNode ParametersSchema);

/// <summary>Result of executing a plugin chat tool - <see cref="IsError"/> mirrors MCP's
/// <c>McpToolCallResult.IsError</c> convention: a failed tool call is reported back to the model as
/// text, not thrown as an exception.</summary>
public sealed record PluginToolResult(bool IsError, string Text);
