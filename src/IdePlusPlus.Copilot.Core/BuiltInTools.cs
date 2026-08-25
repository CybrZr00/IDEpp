namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Every tool the IDE implements itself, as opposed to those coming from an MCP server or a plugin.
///
/// This exists so that adding a tool is one edit rather than two. Tool-call routing previously
/// asked WorkspaceTools.IsWorkspaceTool, and anything that failed that predicate went straight out
/// to the MCP server manager - so a new tool added to the list sent to the model but forgotten in
/// the predicate came back to the model as "Unknown MCP tool", with its handler never reached and
/// its unreachable default arm looking perfectly correct. Routing off the same list that is sent to
/// the model removes the chance to disagree.
/// </summary>
public static class BuiltInTools
{
    public static IReadOnlyList<ToolDefinition> All { get; } =
        [.. WorkspaceTools.All, .. GitTools.All, .. BuildTools.All];

    private static readonly HashSet<string> Names = All.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    public static bool IsBuiltIn(string toolName) => Names.Contains(toolName);
}
