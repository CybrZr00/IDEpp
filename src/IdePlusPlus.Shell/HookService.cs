using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Infrastructure.Hooks;
using Serilog;

namespace IdePlusPlus.Shell;

/// <summary>A solution's checked-in hook config, awaiting the user's decision on whether to run it.
/// <see cref="CommandLines"/> is what would be executed, so the prompt can show real commands.</summary>
public sealed record HookTrustRequest(string SolutionDirectory, string ConfigPath, IReadOnlyList<string> CommandLines);

/// <summary>
/// Claude Code-style hooks for the copilot: user commands that run before/after a tool call and can
/// veto it or hand feedback back to the model.
///
/// Two config files, same `hooks` JSON as Claude Code:
///   %LOCALAPPDATA%\IdePlusPlus\settings.json        - the user's own; always trusted
///   &lt;solution&gt;\.ideplusplus\copilot\settings.json  - meant to be checked in, so gated exactly like
///                                                     a project-local MCP config: the user sees the
///                                                     commands and approves them, and is asked again
///                                                     whenever the file changes.
/// A checked-in hook is arbitrary code execution triggered by the model editing a file, so it must
/// never run on the strength of having been cloned.
///
/// Tool names are matched under both IDE++'s own name and the Claude Code equivalent (propose_edit
/// is "Write" for whole_file and "Edit" otherwise, run_command is "Bash", read_file is "Read"), so a
/// matcher like "Write|Edit|MultiEdit|NotebookEdit" written for Claude Code works unchanged.
/// </summary>
public sealed class HookService
{
    public const string SettingsFileName = "settings.json";

    private static readonly string UserSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", SettingsFileName);

    private static readonly string TrustFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "hooks-project-trust.json");

    private readonly McpProjectTrustStore _trustStore = new(TrustFilePath);
    private readonly HookCommandRunner _runner = new();
    private readonly string _sessionId = Guid.NewGuid().ToString();

    private HookConfiguration _configuration = HookConfiguration.Empty;
    private Task _loadTask = Task.CompletedTask;
    private string? _solutionDirectory;

    /// <summary>Asked once per solution and config version, before its project-local hooks may run.
    /// Unset, project-local hooks are skipped - an unattended process must not approve code.</summary>
    public Func<HookTrustRequest, Task<bool>>? ProjectTrustPrompt { get; set; }

    public event Action<string>? OnLog;

    public static string ProjectSettingsPath(string solutionDirectory) =>
        Path.Combine(solutionDirectory, ".ideplusplus", "copilot", SettingsFileName);

    /// <summary>Whether any hook could fire for this event. True while the config is still loading
    /// (it may be waiting on a trust prompt), so a caller never skips hooks that are about to exist.</summary>
    public bool MayHaveHooks(string eventName) =>
        !_loadTask.IsCompleted || _configuration.HasHooksFor(eventName);

    public void SetSolution(string? solutionDirectory)
    {
        _solutionDirectory = solutionDirectory;
        _configuration = HookConfiguration.Empty;
        _loadTask = solutionDirectory is null ? Task.CompletedTask : LoadAsync(solutionDirectory);
    }

    private async Task LoadAsync(string solutionDirectory)
    {
        try
        {
            var user = LoadFile(UserSettingsPath);

            var project = HookConfiguration.Empty;
            var projectPath = ProjectSettingsPath(solutionDirectory);
            if (File.Exists(projectPath))
            {
                project = await LoadProjectConfigAsync(solutionDirectory, projectPath).ConfigureAwait(true);
            }

            // Guard against the user switching solutions while a trust prompt was open.
            if (string.Equals(_solutionDirectory, solutionDirectory, StringComparison.Ordinal))
            {
                _configuration = HookConfiguration.Combine(user, project);
                foreach (var warning in _configuration.Warnings)
                {
                    OnLog?.Invoke($"[Hooks] {warning}");
                }

                if (!_configuration.IsEmpty)
                {
                    OnLog?.Invoke("[Hooks] Loaded hooks for " + solutionDirectory);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Loading hooks failed");
            OnLog?.Invoke($"[Hooks] Could not load hooks: {ex.Message}");
        }
    }

    private HookConfiguration LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return HookConfiguration.Empty;
        }

        try
        {
            return HookConfiguration.Parse(File.ReadAllText(path), path);
        }
        catch (IOException ex)
        {
            OnLog?.Invoke($"[Hooks] Could not read {path}: {ex.Message}");
            return HookConfiguration.Empty;
        }
    }

    private async Task<HookConfiguration> LoadProjectConfigAsync(string solutionDirectory, string projectPath)
    {
        string text;
        try
        {
            text = File.ReadAllText(projectPath);
        }
        catch (IOException ex)
        {
            OnLog?.Invoke($"[Hooks] Could not read {projectPath}: {ex.Message}");
            return HookConfiguration.Empty;
        }

        var parsed = HookConfiguration.Parse(text, projectPath);
        if (parsed.IsEmpty)
        {
            // Nothing to run, so nothing to approve - but surface why (a typo'd config).
            return parsed;
        }

        var fingerprint = McpProjectTrustStore.Fingerprint(text);
        if (_trustStore.IsTrusted(solutionDirectory, fingerprint))
        {
            return parsed;
        }

        if (ProjectTrustPrompt is not { } prompt)
        {
            OnLog?.Invoke($"[Hooks] Skipped {projectPath} - its hooks have not been approved for this solution.");
            return HookConfiguration.Empty;
        }

        if (!await prompt(new HookTrustRequest(solutionDirectory, projectPath, DescribeCommands(text))).ConfigureAwait(true))
        {
            OnLog?.Invoke($"[Hooks] Declined {projectPath} - its hooks will not run.");
            return HookConfiguration.Empty;
        }

        _trustStore.Trust(solutionDirectory, fingerprint);
        OnLog?.Invoke($"[Hooks] Approved {projectPath}.");
        return parsed;
    }

    /// <summary>The command lines a config would run, for the trust prompt. Re-derived from the raw
    /// JSON rather than the parsed model so the prompt shows what the file says, event by event.</summary>
    private static IReadOnlyList<string> DescribeCommands(string json)
    {
        var lines = new List<string>();
        try
        {
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            foreach (var (eventName, groups) in root?["hooks"] as JsonObject ?? [])
            {
                foreach (var group in groups as JsonArray ?? [])
                {
                    var matcher = group?["matcher"]?.ToString();
                    foreach (var hook in group?["hooks"] as JsonArray ?? [])
                    {
                        var args = hook?["args"] is JsonArray a ? " " + string.Join(' ', a.Select(x => x?.ToString())) : "";
                        lines.Add($"{eventName}{(string.IsNullOrEmpty(matcher) ? "" : $" [{matcher}]")}:  {hook?["command"]}{args}");
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Unparseable configs are empty and never reach the prompt.
        }

        return lines;
    }

    /// <summary>
    /// Runs every hook for <paramref name="eventName"/> whose matcher accepts this tool, in order.
    /// <paramref name="toolInput"/> uses IDE++'s own argument names; the Claude Code-style aliases
    /// (file_path, content, old_string, new_string, command) are added to what the hook sees.
    /// Never throws for a misbehaving hook - a broken hook is reported, not fatal to the turn.
    /// </summary>
    public async Task<HookDecision> RunAsync(
        string eventName,
        string toolName,
        JsonObject toolInput,
        string? toolResultText,
        bool toolFailed,
        CancellationToken cancellationToken)
    {
        await _loadTask.ConfigureAwait(false);

        var directory = _solutionDirectory;
        if (directory is null || !_configuration.HasHooksFor(eventName))
        {
            return HookDecision.None;
        }

        var (names, payloadName, hookInput, filePath) = Describe(toolName, toolInput, directory);
        var commands = _configuration.CommandsFor(eventName, names);
        if (commands.Count == 0)
        {
            return HookDecision.None;
        }

        var payload = new JsonObject
        {
            ["session_id"] = _sessionId,
            ["cwd"] = directory,
            ["hook_event_name"] = eventName,
            ["tool_name"] = payloadName,
            ["ideplusplus_tool_name"] = toolName,
            ["tool_input"] = hookInput,
        };

        if (eventName == HookEvents.PostToolUse)
        {
            var response = new JsonObject { ["success"] = !toolFailed, ["output"] = toolResultText ?? "" };
            if (filePath is not null)
            {
                response["filePath"] = filePath;
            }

            payload["tool_response"] = response;
        }

        var payloadJson = payload.ToJsonString();
        var environment = new Dictionary<string, string>
        {
            ["CLAUDE_PROJECT_DIR"] = directory,
            ["IDEPLUSPLUS_PROJECT_DIR"] = directory,
        };

        var block = false;
        var feedback = new List<string>();
        var notices = new List<string>();

        foreach (var command in commands)
        {
            var resolved = Substitute(command, directory);
            var label = $"'{command.Command}{(command.Args is { Count: > 0 } ? " " + string.Join(' ', command.Args) : "")}'";

            HookRunResult result;
            try
            {
                result = await _runner.RunAsync(resolved, payloadJson, directory, environment, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                notices.Add($"Hook {label} could not run: {ex.Message}");
                continue;
            }

            var decision = HookDecision.Interpret(result, label);
            if (decision.Notice is not null)
            {
                notices.Add(decision.Notice);
            }

            if (decision.Feedback is not null)
            {
                feedback.Add(decision.Feedback);
            }

            if (decision.Block)
            {
                block = true;

                // A veto ends a PreToolUse chain; later hooks would be vetting a call that will not happen.
                if (eventName == HookEvents.PreToolUse)
                {
                    break;
                }
            }
        }

        foreach (var notice in notices)
        {
            OnLog?.Invoke($"[Hooks] {notice}");
        }

        return new HookDecision(
            block,
            feedback.Count > 0 ? string.Join("\n", feedback) : null,
            notices.Count > 0 ? string.Join("\n", notices) : null);
    }

    private static HookCommand Substitute(HookCommand command, string directory)
    {
        string Replace(string text) => text
            .Replace("${CLAUDE_PROJECT_DIR}", directory, StringComparison.Ordinal)
            .Replace("${IDEPLUSPLUS_PROJECT_DIR}", directory, StringComparison.Ordinal);

        return command with { Command = Replace(command.Command), Args = command.Args?.Select(Replace).ToList() };
    }

    /// <summary>The names a tool can be matched under, the name and input shape hook scripts see, and
    /// the absolute file it touches (if any).</summary>
    private static (IReadOnlyList<string> Names, string PayloadName, JsonObject Input, string? FilePath) Describe(
        string toolName, JsonObject input, string directory)
    {
        var hookInput = (JsonObject)input.DeepClone();

        string? Absolute(string? path) => string.IsNullOrWhiteSpace(path)
            ? null
            : Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(directory, path));

        switch (toolName)
        {
            case "propose_edit":
            {
                var mode = input["mode"]?.ToString();
                var alias = mode == "whole_file" ? "Write" : "Edit";
                var filePath = Absolute(input["file"]?.ToString());
                hookInput["file_path"] = filePath;
                if (mode == "whole_file")
                {
                    hookInput["content"] = input["new_text"]?.ToString();
                }
                else
                {
                    hookInput["old_string"] = input["old_text"]?.ToString();
                    hookInput["new_string"] = input["new_text"]?.ToString();
                }

                return ([alias, toolName], alias, hookInput, filePath);
            }

            case "run_command":
                hookInput["description"] = input["reason"]?.ToString();
                return (["Bash", "PowerShell", toolName], "Bash", hookInput, null);

            case "read_file":
            {
                var filePath = Absolute(input["file"]?.ToString());
                hookInput["file_path"] = filePath;
                return (["Read", toolName], "Read", hookInput, filePath);
            }

            case "search_codebase":
                hookInput["pattern"] = input["query"]?.ToString();
                return (["Grep", toolName], "Grep", hookInput, null);

            case "fetch_url":
                return (["WebFetch", toolName], "WebFetch", hookInput, null);

            default:
                return ([toolName], toolName, hookInput, null);
        }
    }
}
