using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The diagnostics tool. Definition only - the handler lives in the chat pane, next to the Roslyn
/// workspace and the editor buffers it needs to overlay.
///
/// Distinct from build_solution in what it costs: this answers "does this one file still compile"
/// in about a second against an already-loaded semantic model, where a build shells out to MSBuild
/// and rebuilds whatever changed. Cheap enough to use after every edit, which is the point.
/// </summary>
public static class DiagnosticTools
{
    public const string GetDiagnosticsToolName = "get_diagnostics";

    public const int MaxReportedDiagnostics = 100;

    public static bool IsDiagnosticTool(string toolName) => toolName is GetDiagnosticsToolName;

    public static readonly ToolDefinition GetDiagnostics = new(
        Name: GetDiagnosticsToolName,
        Description: "Check one C# file for compiler errors and warnings, without running a build. Much faster than " +
                     "build_solution, so use it to check a file you have just edited. It analyses the editor's " +
                     "current text including unsaved changes. It only sees one file at a time and cannot catch " +
                     "errors your change caused elsewhere - run build_solution before you consider the work done.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "Path to the C# file, relative to the solution directory." }
              },
              "required": ["file"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [GetDiagnostics];
}
