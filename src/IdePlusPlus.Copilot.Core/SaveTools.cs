using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The save tool. Definition only - the handler lives in the chat pane, next to the editor
/// documents it writes out.
///
/// An applied edit to a file that is open in the editor changes the editor's buffer and nothing
/// else, so the file on disk is stale until something saves it. Before this tool, the only thing the
/// model could call that happened to save was build_solution (and run_tests / publish_project), so it
/// built just to flush - slow, noisy, and useless for a file in a project that does not compile.
/// </summary>
public static class SaveTools
{
    public const string SaveFilesToolName = "save_files";

    public static bool IsSaveTool(string toolName) => toolName is SaveFilesToolName;

    public static readonly ToolDefinition SaveFiles = new(
        Name: SaveFilesToolName,
        Description: "Write files that are open in the editor with unsaved changes to disk. Edits you make to a " +
                     "file the user has open change the editor's copy only, so the file on disk is out of date " +
                     "until it is saved - call this when something outside the editor needs to see your changes " +
                     "(a command you run, git_diff, another tool reading from disk). You do not need it before " +
                     "build_solution, run_tests or publish_project, which save first themselves. Only files inside " +
                     "the open solution that are already open in the editor are written; it never creates files " +
                     "or touches anything else. Omit 'paths' to save every open file with unsaved changes.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "paths": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Optional files to save, relative to the open solution's directory. Omit to save every open file that has unsaved changes."
                }
              }
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [SaveFiles];
}
