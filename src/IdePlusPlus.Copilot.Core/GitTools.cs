using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Read-only git tools. Definitions only - the handler lives in the chat pane, which is where
/// GitService and the open solution's directory both are.
///
/// These exist mainly so the model can check its own work. An edit that quietly dropped 300 lines
/// is invisible in a tool result that says "applied", but obvious in a diff; and after several
/// edits in one turn, a diff is the only account of what actually changed that does not come from
/// the model's own memory of what it meant to do.
/// </summary>
public static class GitTools
{
    public const string DiffToolName = "git_diff";
    public const string StatusToolName = "git_status";

    /// <summary>Diffs can be enormous - a cap keeps one call from consuming the whole context.</summary>
    public const int MaxOutputChars = 40_000;

    public static bool IsGitTool(string toolName) =>
        toolName is DiffToolName or StatusToolName;

    public static readonly ToolDefinition Diff = new(
        Name: DiffToolName,
        Description: "Show a unified diff of uncommitted changes in the open solution's git repository. " +
                     "Use it to check what your edits actually did - line counts in an edit result tell you " +
                     "how much changed, this tells you what. Untracked (brand new) files do not appear in a " +
                     "diff; use git_status to see those.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Optional path relative to the solution directory, to limit the diff to one file or folder. Omit for every change in the working tree." },
                "staged": { "type": "boolean", "description": "Diff what is staged against HEAD instead of the unstaged working-tree changes. Defaults to false." }
              }
            }
            """)!);

    public static readonly ToolDefinition Status = new(
        Name: StatusToolName,
        Description: "Show the current branch and which files are modified, staged, or untracked in the open " +
                     "solution's git repository. Cheaper than git_diff when you only need to know which files " +
                     "changed rather than how.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {}
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [Diff, Status];
}
