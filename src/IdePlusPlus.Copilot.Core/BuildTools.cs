using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The build tool. Definition only - the handler lives in the chat pane, alongside BuildService,
/// the open solution's path, and the editor documents that have to be written to disk first.
///
/// This is the signal that turns editing into something the model can check. A compiler error is
/// the cheapest possible evidence that an edit went wrong - including the specific way this IDE's
/// edits used to go wrong, where content vanished and nothing said so.
/// </summary>
public static class BuildTools
{
    public const string BuildToolName = "build_solution";

    /// <summary>A failing build can emit thousands of lines; only the diagnostics are worth the
    /// context, and past a point even those stop being actionable in one turn.</summary>
    public const int MaxReportedDiagnostics = 50;

    public const int MaxRawOutputChars = 8_000;

    public static bool IsBuildTool(string toolName) => toolName is BuildToolName;

    public static readonly ToolDefinition Build = new(
        Name: BuildToolName,
        Description: "Build the open solution, or one project in it, and return the compiler errors and warnings. " +
                     "Use it after making changes to check they actually compile - this is the only way to find " +
                     "out whether an edit worked, rather than assuming it did. Files with unsaved changes are " +
                     "written to disk first, so the build always reflects the edits you have made. Builds the " +
                     "configuration the user has selected in the IDE.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "project": { "type": "string", "description": "Optional path to a single .csproj, relative to the solution directory, to build just that project. Omit to build the whole solution, which is usually what you want after changing more than one file." }
              }
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [Build];
}
