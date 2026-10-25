using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.SourceControl;
using IdePlusPlus.TestRunner;

namespace IdePlusPlus.Shell.Team;

/// <summary>
/// Runs one Ultra worker's tool calls against that worker's own worktree.
///
/// Deliberately WPF-free, like ReadOnlyToolDispatcher, which it delegates the read-only tools to:
/// several of these run at once on the thread pool, and none may need the UI thread. The one tool
/// that does - run_command, whose approval is a dialog - comes in as a delegate from the chat pane,
/// which marshals it itself.
///
/// Each worker gets its own BuildService and TestRunService. Those are one-run-at-a-time guards
/// over a single obj/bin, and each worktree has its own obj/bin, so sharing the IDE's instances
/// would serialize the whole team behind one build for no reason. What is shared is
/// <c>buildSlots</c>, which caps how many builds and test runs the team has going at once - each one
/// is a full MSBuild, and four in parallel on a laptop helps nobody.
///
/// The user's PreToolUse/PostToolUse hooks are not run here: they guard the user's checkout, and
/// nothing a worker does touches it until the run's result is reviewed, which does go through them.
/// </summary>
public sealed class TeamToolDispatcher
{
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    private readonly TeamWorkerContext _context;
    private readonly TeamWorkerState _state;
    private readonly TeamBoard _board;
    private readonly TeamMessageBus _bus;
    private readonly ReadOnlyToolDispatcher _readOnlyTools;
    private readonly GitService _gitService;
    private readonly TeamGitWorkspace _teamGit;
    private readonly SemaphoreSlim _buildSlots;
    private readonly string _configuration;
    private readonly Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? _runCommand;
    private readonly BuildService _buildService = new();
    private readonly TestRunService _testRunService = new();

    /// <summary>True from any change to the worktree until a build passes - so committing right
    /// after a passing build can record that commit as known-good without building it again.</summary>
    private bool _changedSinceBuild = true;

    private bool _warnedAboutUnmergedWork;

    /// <param name="runCommand">run_command's implementation - (command, reason, working directory,
    /// token) - or null when shell commands are off for this run.</param>
    public TeamToolDispatcher(
        TeamWorkerContext context,
        TeamWorkerState state,
        TeamBoard board,
        TeamMessageBus bus,
        ReadOnlyToolDispatcher readOnlyTools,
        GitService gitService,
        TeamGitWorkspace teamGit,
        SemaphoreSlim buildSlots,
        string configuration,
        Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? runCommand)
    {
        _context = context;
        _state = state;
        _board = board;
        _bus = bus;
        _readOnlyTools = readOnlyTools;
        _gitService = gitService;
        _teamGit = teamGit;
        _buildSlots = buildSlots;
        _configuration = configuration;
        _runCommand = runCommand;
    }

    /// <summary>The tools this worker is offered - the same list ExecuteAsync routes.</summary>
    public IReadOnlyList<ToolDefinition> Tools =>
        TeamTools.ForWorker(TeamEditTool.Definition, shellCommandsEnabled: _runCommand is not null);

    public string? DrainNotices() => TeamNotices.Compose(_context.Name, _board, _bus);

    public async Task<(bool IsError, string Text)> ExecuteAsync(
        string toolName, JsonNode? arguments, CancellationToken cancellationToken)
    {
        _state.RecordStep(toolName);
        try
        {
            return await ExecuteCoreAsync(toolName, arguments, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A git or file-system failure is something the worker can read and work around; letting
            // it escape would end the worker's cycle for one bad call.
            return (true, $"{toolName} failed: {ex.Message}");
        }
    }

    private async Task<(bool IsError, string Text)> ExecuteCoreAsync(
        string toolName, JsonNode? arguments, CancellationToken cancellationToken)
    {
        string? Arg(string name) => arguments?[name]?.GetValue<string>();

        switch (toolName)
        {
            case WorkspaceTools.ReadFileToolName:
            case WorkspaceTools.ListFilesToolName:
            case WorkspaceTools.SearchCodebaseToolName:
            case SymbolTools.FindSymbolToolName:
            case SymbolTools.FindReferencesToolName:
            case DiagnosticTools.GetDiagnosticsToolName:
                return await _readOnlyTools.ExecuteAsync(toolName, arguments, _context.SolutionDirectory, cancellationToken);

            case TeamTools.EditFileToolName:
                return EditFile(arguments);

            case GitTools.DiffToolName:
                return await GitDiffAsync(Arg("path"), arguments?["staged"]?.GetValue<bool>() ?? false, cancellationToken);

            case GitTools.StatusToolName:
                var (statusOk, status) = await _gitService.GetShortStatusAsync(_context.WorktreeRoot, cancellationToken);
                return statusOk
                    ? (false, status.Length == 0 ? "Working tree clean." : ToolReports.Cap(status, GitTools.MaxOutputChars, "status"))
                    : (true, $"git status failed: {status}");

            case BuildTools.BuildToolName:
                return await BuildAsync(Arg("project"), cancellationToken);

            case TestTools.RunTestsToolName:
                return await RunTestsAsync(Arg("project"), Arg("filter"), cancellationToken);

            case ShellTools.RunCommandToolName when _runCommand is not null:
                var command = Arg("command");
                if (string.IsNullOrWhiteSpace(command))
                {
                    return (true, "run_command requires a 'command' argument.");
                }

                _changedSinceBuild = true;
                return await _runCommand(command, Arg("reason") ?? "(no reason given)", _context.SolutionDirectory, cancellationToken);

            case TeamTools.BoardReadToolName:
                return (false, _board.Read());

            case TeamTools.BoardWriteToolName:
                return BoardWrite(Arg("type"), Arg("text"), Arg("detail"));

            case TeamTools.BoardGrepToolName:
                return (false, _board.Grep(Arg("query")));

            case TeamTools.BoardUnfoldToolName:
                return (false, _board.Unfold(Arg("id")));

            case TeamTools.PostToolName:
                if (_bus.Post(_context.Name, Arg("text")) is { } postError)
                {
                    return (true, postError);
                }

                _state.HadVisibleActivityThisCycle = true;
                return (false, "Posted to the team channel.");

            case TeamTools.DirectMessageToolName:
                return _bus.SendDirect(_context.Name, Arg("to"), Arg("text")) is { } dmError
                    ? (true, dmError)
                    : (false, $"Sent to {Arg("to")?.Trim()}.");

            case TeamTools.CommitToolName:
                return await CommitAsync(Arg("message"), cancellationToken);

            case TeamTools.SyncToolName:
                return await SyncAsync(cancellationToken);

            case TeamTools.MergeToolName:
                return await MergeAsync(cancellationToken);

            case TeamTools.DoneToolName:
                return await DoneAsync(Arg("summary"), cancellationToken);

            default:
                return (true, $"'{toolName}' is not available to an Ultra worker.");
        }
    }

    private (bool IsError, string Text) EditFile(JsonNode? arguments)
    {
        var file = arguments?["file"]?.GetValue<string>();
        var mode = arguments?["mode"]?.GetValue<string>();
        var newText = arguments?["new_text"]?.GetValue<string>();
        if (file is null || mode is null || newText is null)
        {
            return (true, "edit_file requires 'file', 'mode' and 'new_text'.");
        }

        if (WorkspaceFileReader.ResolveWithinRoot(_context.SolutionDirectory, file) is not { } fullPath)
        {
            return (true, $"Refused: '{file}' resolves outside your worktree's solution directory.");
        }

        var operation = new EditOperation(
            fullPath,
            mode,
            arguments?["start_line"]?.GetValue<int>(),
            arguments?["end_line"]?.GetValue<int>(),
            newText,
            arguments?["old_text"]?.GetValue<string>());

        var exists = File.Exists(fullPath);
        if (!exists && !operation.IsWholeFile)
        {
            return (true, $"{file} does not exist. Use mode 'whole_file' to create it.");
        }

        var (current, encoding) = exists ? ReadPreservingEncoding(fullPath) : ("", new UTF8Encoding(false));
        if (!EditApplicationService.TryComputeResultingContent(operation, current, out var result, out var error))
        {
            return (true, $"Edit to {file} not applied: {error}");
        }

        // TryComputeResultingContent works in '\n'. A file that used CRLF keeps it, so the diff a
        // peer or the user later sees is the change, not every line of the file.
        if (current.Contains("\r\n", StringComparison.Ordinal))
        {
            result = result.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, result, encoding);
        _changedSinceBuild = true;

        var linesBefore = exists ? CountLines(current) : 0;
        var linesAfter = CountLines(result);
        return (false, exists
            ? $"Edited {file} in your worktree ({linesBefore} -> {linesAfter} lines)."
            : $"Created {file} in your worktree ({linesAfter} lines).");
    }

    private async Task<(bool IsError, string Text)> GitDiffAsync(string? path, bool staged, CancellationToken cancellationToken)
    {
        if (path is not null && WorkspaceFileReader.ResolveWithinRoot(_context.SolutionDirectory, path) is null)
        {
            return (true, $"Refused: '{path}' resolves outside your worktree's solution directory.");
        }

        var (success, output) = await _gitService.GetDiffAsync(_context.SolutionDirectory, path, staged, cancellationToken);
        if (!success)
        {
            return (true, $"git diff failed: {output}");
        }

        return (false, output.Length == 0
            ? "No uncommitted changes. A diff never lists untracked files - use git_status for those."
            : ToolReports.Cap(output, GitTools.MaxOutputChars, "diff"));
    }

    private async Task<(bool IsError, string Text)> BuildAsync(string? project, CancellationToken cancellationToken)
    {
        var target = _context.SolutionPath;
        if (!string.IsNullOrWhiteSpace(project))
        {
            if (WorkspaceFileReader.ResolveWithinRoot(_context.SolutionDirectory, project) is not { } projectPath
                || !File.Exists(projectPath))
            {
                return (true, $"Project not found in your worktree: {project}");
            }

            target = projectPath;
        }

        var (exitCode, report) = await RunBuildAsync(target, cancellationToken);
        if (exitCode == 0 && target == _context.SolutionPath)
        {
            _changedSinceBuild = false;
            if (!await _teamGit.HasUncommittedChangesAsync(_context.WorktreeRoot, cancellationToken))
            {
                _state.LastGoodCommit = await _teamGit.GetHeadAsync(_context.WorktreeRoot, cancellationToken);
            }
        }

        return (exitCode != 0, report);
    }

    /// <summary>Only a whole-solution build counts towards "known to build" - a single project
    /// passing says nothing about what depends on it.</summary>
    private async Task<(int ExitCode, string Report)> RunBuildAsync(string target, CancellationToken cancellationToken)
    {
        var outputLines = new List<string>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(BuildTimeout);

        await _buildSlots.WaitAsync(cancellationToken);
        try
        {
            var exitCode = await _buildService.BuildAsync(
                target, line => { lock (outputLines) { outputLines.Add(line); } },
                configuration: _configuration, cancellationToken: timeout.Token);
            return (exitCode, ToolReports.FormatBuildReport(exitCode, _configuration, [], outputLines));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-1, $"The build was still running after {BuildTimeout.TotalMinutes:0} minutes and was stopped.");
        }
        finally
        {
            _buildSlots.Release();
        }
    }

    private async Task<(bool IsError, string Text)> RunTestsAsync(
        string? project, string? filter, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projects;
        if (!string.IsNullOrWhiteSpace(project))
        {
            if (WorkspaceFileReader.ResolveWithinRoot(_context.SolutionDirectory, project) is not { } projectPath
                || !File.Exists(projectPath))
            {
                return (true, $"Project not found in your worktree: {project}");
            }

            projects = [projectPath];
        }
        else if (_context.TestProjectPaths.Count == 0)
        {
            return (true, "No test projects were found in this solution.");
        }
        else
        {
            projects = _context.TestProjectPaths;
        }

        await _buildSlots.WaitAsync(cancellationToken);
        try
        {
            return await ToolReports.RunTestProjectsAsync(_testRunService, projects, filter, TestTimeout, cancellationToken);
        }
        finally
        {
            _buildSlots.Release();
        }
    }

    private (bool IsError, string Text) BoardWrite(string? type, string? text, string? detail)
    {
        var (entry, error) = _board.Write(_context.Name, type, text, detail);
        if (entry is null)
        {
            return (true, error!);
        }

        _state.HadVisibleActivityThisCycle = true;
        _state.WroteToBoardThisCycle = true;
        if (entry.Type == TeamBoardEntryType.Claim)
        {
            _state.CurrentClaim = entry.Text;
        }
        else if (entry.Type == TeamBoardEntryType.PatchSummary)
        {
            _state.CurrentClaim = null;
        }

        return (false, $"Posted {entry.Id}.");
    }

    private async Task<(bool IsError, string Text)> CommitAsync(string? message, CancellationToken cancellationToken)
    {
        var result = await _teamGit.CommitAllAsync(
            _context.WorktreeRoot,
            string.IsNullOrWhiteSpace(message) ? $"{_context.Name}: work in progress" : message.Trim(),
            _context.AuthorName,
            cancellationToken);

        switch (result.Status)
        {
            case TeamCommitStatus.Committed:
                if (!_changedSinceBuild)
                {
                    _state.LastGoodCommit = result.Commit;
                }

                return (false, $"Committed {Short(result.Commit)} to your branch.");

            case TeamCommitStatus.NothingToCommit:
                return (false, "Nothing to commit - your worktree has no changes since your last commit.");

            default:
                return (true, $"Commit failed: {result.Message}");
        }
    }

    private async Task<(bool IsError, string Text)> SyncAsync(CancellationToken cancellationToken)
    {
        var result = await _teamGit.MergeBranchIntoWorktreeAsync(
            _context.WorktreeRoot, _context.TeamBranch, _context.AuthorName, cancellationToken);

        switch (result.Status)
        {
            case TeamSyncStatus.UpToDate:
                return (false, "Already up to date with the team branch.");

            case TeamSyncStatus.Merged:
                _changedSinceBuild = true;
                return (false, "Merged the latest team branch into yours. Peers' changes are now in your worktree - "
                               + "rebuild before you team_merge.");

            case TeamSyncStatus.Conflicted:
                _changedSinceBuild = true;
                return (true,
                    "The team branch conflicts with your work in: " + string.Join(", ", result.ConflictedPaths)
                    + ". Those files contain git's conflict markers (<<<<<<<, =======, >>>>>>>). Resolve each with "
                    + "edit_file keeping both sides' intent, build_solution, then team_commit to conclude the merge.\n\n"
                    + result.Message);

            default:
                return (true, $"Sync failed: {result.Message}");
        }
    }

    private async Task<(bool IsError, string Text)> MergeAsync(CancellationToken cancellationToken)
    {
        if (await _teamGit.HasUncommittedChangesAsync(_context.WorktreeRoot, cancellationToken))
        {
            return (true, "You have uncommitted changes. team_commit first - team_merge merges commits.");
        }

        var head = await _teamGit.GetHeadAsync(_context.WorktreeRoot, cancellationToken);
        var teamHead = await _teamGit.ResolveRefAsync(_context.RepositoryRoot, _context.TeamBranch, cancellationToken)
                       ?? throw new InvalidOperationException($"The team branch {_context.TeamBranch} is missing.");

        if (head == teamHead)
        {
            return (false, "Nothing to merge - your branch is exactly the team branch.");
        }

        if (!await _teamGit.IsAncestorAsync(_context.RepositoryRoot, teamHead, head, cancellationToken))
        {
            return (true, "The team branch has moved on since you last synced. team_sync, rebuild, then team_merge again.");
        }

        if (_state.LastGoodCommit != head)
        {
            var (exitCode, report) = await RunBuildAsync(_context.SolutionPath, cancellationToken);
            if (exitCode != 0)
            {
                return (true, "Not merged - your commit does not build, and the team branch must keep building.\n\n" + report);
            }

            _changedSinceBuild = false;
            _state.LastGoodCommit = head;
        }

        var result = await _teamGit.FastForwardBranchAsync(
            _context.RepositoryRoot, _context.TeamBranch, head, teamHead, cancellationToken);
        if (result.Status != TeamFastForwardStatus.FastForwarded)
        {
            return (true, result.Message);
        }

        _state.RecordMerge();
        _state.HadVisibleActivityThisCycle = true;
        _bus.Post(_context.Name, $"Merged {Short(head)} into the team branch.");
        return (false, $"Merged {Short(head)} into the team branch. Post a PATCH_SUMMARY for it.");
    }

    private async Task<(bool IsError, string Text)> DoneAsync(string? summary, CancellationToken cancellationToken)
    {
        if (!_warnedAboutUnmergedWork && await DescribeUnmergedWorkAsync(cancellationToken) is { } unmerged)
        {
            _warnedAboutUnmergedWork = true;
            return (true,
                $"You still have {unmerged}, and anything unmerged is lost when the run ends. Merge it first. If it "
                + "cannot be merged, post a FAIL saying why, then call team_done again.");
        }

        _state.DoneSummary = string.IsNullOrWhiteSpace(summary) ? "(no summary)" : summary.Trim();
        _state.CurrentClaim = null;
        _state.Phase = TeamWorkerPhase.Done;
        _board.ReleaseClaims(_context.Name);
        _bus.Post(_context.Name, $"Done: {_state.DoneSummary}");
        return (false, "Recorded - you are done. Stop here: reply with a one-line summary and make no more tool calls.");
    }

    private async Task<string?> DescribeUnmergedWorkAsync(CancellationToken cancellationToken)
    {
        if (await _teamGit.HasUncommittedChangesAsync(_context.WorktreeRoot, cancellationToken))
        {
            return "uncommitted changes";
        }

        var head = await _teamGit.GetHeadAsync(_context.WorktreeRoot, cancellationToken);
        return await _teamGit.IsAncestorAsync(_context.RepositoryRoot, head, _context.TeamBranch, cancellationToken)
            ? null
            : "commits that are not on the team branch";
    }

    private static (string Text, Encoding Encoding) ReadPreservingEncoding(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes is [0xEF, 0xBB, 0xBF, ..];
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom);
        return (encoding.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0)), encoding);
    }

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";
}
