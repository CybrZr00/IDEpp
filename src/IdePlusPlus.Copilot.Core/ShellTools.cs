using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The escape hatch: an arbitrary PowerShell command.
///
/// Deliberately the last tool added and the only one that is off by default. The typed tools cover
/// what the agent actually needs day to day - build, test, diagnostics, git, symbols, files - and
/// each returns structured data rather than text to be re-parsed. This exists for the things none
/// of them cover, and it is not a substitute for any of them.
///
/// It is also the one tool that turns a prompt injection into code execution: a README the model
/// reads is data, but a shell makes acting on it possible. Hence off by default, and every distinct
/// command confirmed by the user before it runs.
/// </summary>
public static class ShellTools
{
    public const string RunCommandToolName = "run_command";

    public const int MaxOutputChars = 20_000;

    public static bool IsShellTool(string toolName) => toolName is RunCommandToolName;

    public static readonly ToolDefinition RunCommand = new(
        Name: RunCommandToolName,
        Description: "Run a PowerShell command in the solution directory and return its output and exit code. " +
                     "The user is asked to confirm each distinct command before it runs, so prefer the purpose-built " +
                     "tools where they fit - build_solution, run_tests, get_diagnostics, git_diff, git_status, " +
                     "read_file, list_files - which need no confirmation and return cleaner results. Use this only " +
                     "for what none of those cover. Commands run non-interactively: anything that waits for input " +
                     "will fail rather than hang.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "The PowerShell command line to run. It runs with the solution directory as the working directory." },
                "reason": { "type": "string", "description": "One short line on why this command is needed. Shown to the user in the confirmation prompt, so write it for them, not for the log." }
              },
              "required": ["command", "reason"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [RunCommand];
}
