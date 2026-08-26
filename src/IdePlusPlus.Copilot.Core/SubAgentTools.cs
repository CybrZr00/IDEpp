using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Lets the model fan out several independent, read-only investigations at once - "compare these
/// three approaches", "check how X is used across these unrelated areas" - instead of researching
/// them one at a time in the same conversation.
///
/// The tool set each sub-agent gets (see AllowedTools) is a hard-coded allow-list, not a filtered
/// copy of whatever the primary loop currently offers: MCP and plugin tools are excluded entirely,
/// since there is no way to statically prove a user-configured MCP server or a plugin-registered
/// tool is actually side-effect-free. A future built-in tool is excluded by default too, unless it
/// is deliberately added here - the allow-list has to be the thing that changes on purpose, not the
/// thing that silently grows alongside BuiltInTools.All.
/// </summary>
public static class SubAgentTools
{
    public const string InvestigateParallelToolName = "investigate_parallel";

    public const int MaxTasks = 5;
    public const int MaxConcurrency = 3;
    public const int MaxIterationsPerTask = 15;

    /// <summary>Exactly what a sub-agent may call - see the type doc for why this is a hard-coded
    /// allow-list rather than a filtered copy of BuiltInTools.All. Every entry here is confirmed
    /// safe to call concurrently: none of their handlers touch the WPF dispatcher, and
    /// RoslynWorkspaceService's own locking only ever protects copying an immutable snapshot
    /// reference, never the search that follows.</summary>
    public static IReadOnlyList<ToolDefinition> AllowedTools { get; } =
    [
        WorkspaceTools.ReadFile,
        WorkspaceTools.ListFiles,
        WorkspaceTools.SearchCodebase,
        SymbolTools.FindSymbol,
        SymbolTools.FindReferences,
        DiagnosticTools.GetDiagnostics,
    ];

    public static bool IsSubAgentTool(string toolName) => toolName is InvestigateParallelToolName;

    public static readonly ToolDefinition InvestigateParallel = new(
        Name: InvestigateParallelToolName,
        Description: "Run up to 5 independent, read-only investigations in parallel and return each one's " +
                     "findings labelled by task. Use this instead of investigating several unrelated things " +
                     "one at a time in this conversation - e.g. comparing approaches, or checking how " +
                     "something is used across unrelated areas of the codebase. Each sub-agent only has " +
                     "read_file, list_files, search_codebase, find_symbol, find_references and " +
                     "get_diagnostics - it cannot edit anything, run commands, or ask a follow-up question, so " +
                     "give each task everything it needs up front. Most useful with a cloud provider - a local " +
                     "model server usually serves one request at a time, so tasks may run one after another " +
                     "rather than truly in parallel.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "tasks": {
                  "type": "array",
                  "items": { "type": "string" },
                  "maxItems": 5,
                  "description": "Each item is one self-contained investigation task, written with enough context that a sub-agent with no memory of this conversation can act on it alone."
                }
              },
              "required": ["tasks"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [InvestigateParallel];
}
