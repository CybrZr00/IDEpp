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
    [
        .. WorkspaceTools.All,
        .. GitTools.All,
        .. BuildTools.All,
        .. TestTools.All,
        .. DiagnosticTools.All,
        .. SymbolTools.All,
        .. SkillTools.All,
        .. SubAgentTools.All,
    ];

    /// <summary>
    /// Tools the IDE implements but does not always offer. run_command is opt-in per session, so it
    /// is absent from <see cref="All"/> - but routing must still recognise it, or a call made while
    /// it was enabled would be forwarded to MCP as an unknown tool.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> OptIn { get; } = [.. ShellTools.All];

    /// <summary>
    /// Gated independently of <see cref="OptIn"/>'s AreShellCommandsEnabled toggle: fetch_url reaches
    /// the network but can never run arbitrary code the way run_command can, so it has its own
    /// AreWebFetchEnabled setting rather than riding on the shell-commands one.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> WebOptIn { get; } = [.. WebTools.All];

    private static readonly HashSet<string> Names =
        All.Concat(OptIn).Concat(WebOptIn).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

    public static bool IsBuiltIn(string toolName) => Names.Contains(toolName);
}
