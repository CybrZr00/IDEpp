using System.Text.Json.Nodes;
using IdePlusPlus.PluginContracts;

namespace IdePlusPlus.Shell;

/// <summary>One chat tool a plugin registered via IPluginHost.RegisterChatTool.</summary>
public sealed record PluginChatToolRegistration(string PluginName, PluginToolDefinition Definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> Execute)
{
    /// <summary>The name the tool is exposed to the chat assistant under - mirrors
    /// <c>McpToolInfo.QualifiedName</c>'s <c>"{ServerName}__{Name}"</c> convention so a plugin tool
    /// can never collide with an MCP tool or another plugin's tool of the same short name.</summary>
    public string QualifiedName => $"plugin__{PluginName}__{Definition.Name}";
}

/// <summary>
/// Tracks chat tools plugins register, keyed by the owning plugin's name (same "ahead of a future
/// unload path" rationale as the other two new plugin registries). This sits directly in
/// CopilotChatPaneViewModel's tool-aggregation/dispatch path - the highest-stakes code path in the
/// app, since it also drives ProposeEditTool's real file-editing flow - so <see cref="ExecuteAsync"/>
/// must never let a plugin's exception propagate past this boundary.
/// </summary>
public sealed class PluginChatToolRegistry
{
    private readonly List<PluginChatToolRegistration> _tools = [];

    public IReadOnlyList<PluginChatToolRegistration> Tools => _tools;

    public void Register(string pluginName, PluginToolDefinition definition, Func<JsonNode?, CancellationToken, Task<PluginToolResult>> execute) =>
        _tools.Add(new PluginChatToolRegistration(pluginName, definition, execute));

    /// <summary>The registered tools, mapped to the assistant's own <see cref="Copilot.Core.ToolDefinition"/>
    /// shape under each tool's <see cref="PluginChatToolRegistration.QualifiedName"/> - ready to be
    /// appended to the same list the assistant builds from its built-in tools and MCP servers'.</summary>
    public IReadOnlyList<Copilot.Core.ToolDefinition> GetToolDefinitions() =>
        _tools.Select(t => new Copilot.Core.ToolDefinition(t.QualifiedName, t.Definition.Description, t.Definition.ParametersSchema)).ToList();

    /// <summary>Whether <paramref name="qualifiedName"/> names a registered plugin tool - callers use
    /// this to route a tool call to <see cref="ExecuteAsync"/> instead of (e.g.) the MCP dispatch
    /// path, before any name-specific handling runs.</summary>
    public bool IsPluginTool(string qualifiedName) => _tools.Any(t => t.QualifiedName == qualifiedName);

    /// <summary>
    /// Invokes the matching registration's execute delegate. Never throws: an unknown
    /// <paramref name="qualifiedName"/> or an exception from the plugin's delegate both come back as
    /// a <see cref="PluginToolResult"/> with <c>IsError: true</c> rather than propagating - a
    /// throwing plugin tool must degrade one chat turn, never the app.
    /// </summary>
    public async Task<PluginToolResult> ExecuteAsync(string qualifiedName, JsonNode? arguments, CancellationToken cancellationToken)
    {
        var registration = _tools.FirstOrDefault(t => t.QualifiedName == qualifiedName);
        if (registration is null)
        {
            return new PluginToolResult(true, $"No plugin tool is registered as '{qualifiedName}'.");
        }

        try
        {
            return await registration.Execute(arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            return new PluginToolResult(true, $"Plugin tool threw: {ex.Message}");
        }
    }
}
