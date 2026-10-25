using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;

namespace IdePlusPlus.Copilot.Diff;

/// <summary>
/// An Ultra worker's edit tool: propose_edit's exact schema - so the same three modes, the same
/// strict replace_text/replace_range rules, and the same wording a model already knows - but
/// written straight to the worker's own worktree instead of becoming a card for the user to accept.
/// Nothing a worker writes reaches the user until the run's result is reviewed, so there is no one
/// to accept it from and nothing to protect by asking.
/// </summary>
public static class TeamEditTool
{
    public static readonly ToolDefinition Definition = new(
        Name: TeamTools.EditFileToolName,
        Description: "Edit a file in your worktree; the change is written to disk immediately. Prefer mode " +
                     "'replace_text': you quote the exact snippet you want gone and its replacement, so it cannot " +
                     "be thrown off by line numbers that moved. Use 'replace_range' only with line numbers from a " +
                     "read_file made since your last edit to that file. Use 'whole_file' to create a new file. " +
                     "Call it once per file.",
        ParametersSchema: ProposeEditTool.Definition.ParametersSchema.DeepClone());
}
