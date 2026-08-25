using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The test tool. Definition only - the handler lives in the chat pane, next to TestRunService and
/// the solution's list of test projects.
///
/// A build proves code compiles; only tests say whether it still does what it did. That matters
/// most for the edits this IDE's copilot makes, where a change can compile perfectly and still have
/// dropped a branch on the way through.
/// </summary>
public static class TestTools
{
    public const string RunTestsToolName = "run_tests";

    /// <summary>Failures are what the model needs; a long tail of them stops being actionable in
    /// one turn, and passing tests need only be counted.</summary>
    public const int MaxReportedFailures = 25;

    public const int MaxFailureMessageChars = 1_500;

    public static bool IsTestTool(string toolName) => toolName is RunTestsToolName;

    public static readonly ToolDefinition RunTests = new(
        Name: RunTestsToolName,
        Description: "Run the solution's tests and return which failed and why. Use it after a change that could " +
                     "alter behaviour - build_solution only proves the code compiles, not that it still does what " +
                     "it did. Narrow the run with 'filter' when you know which tests are relevant; a whole-solution " +
                     "run can take minutes.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "project": { "type": "string", "description": "Optional path to a test project, relative to the solution directory. Omit to run every test project in the solution." },
                "filter": { "type": "string", "description": "Optional substring of the fully-qualified test name, matched as dotnet test's FullyQualifiedName~value. A class name or a method name both work." }
              }
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [RunTests];
}
