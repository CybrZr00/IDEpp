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
    private static readonly TimeSpan DefaultBuildTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Waits this long or more for a free build slot are mentioned in the build report, so
    /// a worker can tell a slow build from a busy team.</summary>
    private static readonly TimeSpan ReportedSlotWait = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _buildTimeout;

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

    /// <summary>
    /// Set for an orchestrated run: asked after a commit builds and before it is fast-forwarded onto
    /// the team branch, with (worker name, team head, this commit). A refusal comes back to the worker
    /// as the lead's feedback and the merge does not happen. Null for an ordinary run.
    /// </summary>
    public Func<string, string, string, CancellationToken, Task<(bool Approved, string Feedback)>>? MergeReview { get; set; }

    /// <param name="runCommand">run_command's implementation - (command, reason, working directory,
    /// token) - or null when shell commands are off for this run.</param>
    /// <param name="buildTimeout">How long one build may run once it has a build slot - not
    /// counting the wait for the slot. Five minutes unless a test needs it shorter.</param>
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
        Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? runCommand,
        TimeSpan? buildTimeout = null)
    {
        _buildTimeout = buildTimeout ?? DefaultBuildTimeout;
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

    /// <summary>
    /// One whole-solution build of the worktree as the run starts, before any worker has done
    /// anything. A pass also records the starting commit as known to build, so this worker's first
    /// team_merge of an unchanged base needs no second build.
    /// </summary>
    public async Task<(bool Succeeded, string Report)> PreflightBuildAsync(CancellationToken cancellationToken)
    {
        var (exitCode, report) = await RunBuildAsync(_context.SolutionPath, cancellationToken);
        if (exitCode == 0)
        {
            _changedSinceBuild = false;
            _state.LastGoodCommit = await _teamGit.GetHeadAsync(_context.WorktreeRoot, cancellationToken);
        }

        return (exitCode == 0, report);
    }

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
                return await _runCommand(
                    command, $"[Ultra {_context.Name}] {Arg("reason") ?? "(no reason given)"}", _context.SolutionDirectory, cancellationToken);

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

        if (RefuseWrite(fullPath, file) is { } refusal)
        {
            return (true, refusal);
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

    /// <summary>
    /// Writes a worker may not make, even inside its own worktree:
    /// - a path Windows would read differently from how it is checked here - an 8.3 short name
    ///   ("GIT~1"), a trailing dot or space (".git."), or an NTFS stream suffix ("x.csproj::$DATA") -
    ///   any of which would carry a write past the two name checks below;
    /// - anything under a .git segment - including the worktree's own .git file, which names the
    ///   repository git uses, and so could point every later git call at a config with hooks;
    /// - anything reached through a symlink or junction, which ResolveWithinRoot cannot see past;
    /// - build-defining files, unless the run allows shell commands, so the most direct way to turn
    ///   an edit into a command (an MSBuild Exec target, run by the next build_solution) needs the
    ///   same opt-in a command does.
    ///
    /// The last one is defence in depth, not a security boundary. Workers write code and build and
    /// test it without review - as Auto Mode does - so code a worker wrote runs on this machine with
    /// Shell on or off. What contains a run is that it works in its own worktree, that the user is
    /// told this before choosing Ultra, and that nothing reaches their checkout unreviewed.
    /// </summary>
    private string? RefuseWrite(string fullPath, string file)
    {
        var relative = Path.GetRelativePath(_context.WorktreeRoot, fullPath);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relative.Contains(':') || relative.Contains('~') || segments.Any(s => s.EndsWith('.') || s.EndsWith(' ')))
        {
            return $"Refused: '{file}' uses a form of path Windows can read as a different file (a '~' short name, "
                   + "a ':' stream, or a name ending in a dot or space). Use the file's ordinary name.";
        }

        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
        {
            return $"Refused: '{file}' is inside git's own metadata, which a worker never edits.";
        }

        for (var path = fullPath; path.Length > _context.WorktreeRoot.Length; path = Path.GetDirectoryName(path)!)
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                return $"Refused: '{file}' is reached through a symbolic link or junction, so where the write "
                       + "would really land cannot be checked.";
            }
        }

        if (_runCommand is null && IsBuildDefiningFile(fullPath))
        {
            return $"Refused: '{file}' defines how the solution builds, and an edit to it can make the next build "
                   + "run a command. Shell commands are off for this run, so build files cannot be changed. If the goal "
                   + "needs this, post a FAIL saying so - the user can rerun with Shell on.";
        }

        return null;
    }

    internal static bool IsBuildDefiningFile(string path)
    {
        var name = Path.GetFileName(path);
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".proj", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".props", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".user", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".rsp", StringComparison.OrdinalIgnoreCase)
               || name.Equals("global.json", StringComparison.OrdinalIgnoreCase)
               || name.Equals("nuget.config", StringComparison.OrdinalIgnoreCase);
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

    /// <summary>
    /// Only a whole-solution build counts towards "known to build" - a single project passing says
    /// nothing about what depends on it.
    ///
    /// The timeout starts once a build slot is free, not when the build is asked for. It used to
    /// start first, so on a big team the wait for one of the shared slots used the build's whole
    /// budget: a 16-worker run's merge-gate builds were "stopped after 5 minutes" without ever
    /// starting, and nothing could merge.
    /// </summary>
    private async Task<(int ExitCode, string Report)> RunBuildAsync(string target, CancellationToken cancellationToken)
    {
        var outputLines = new List<string>();
        var queuedAt = DateTime.UtcNow;
        await _buildSlots.WaitAsync(cancellationToken);
        var waited = DateTime.UtcNow - queuedAt;
        var waitNote = waited >= ReportedSlotWait
            ? $" (Waited {FormatDuration(waited)} for a free build slot first - the team shares them.)"
            : "";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_buildTimeout);
        try
        {
            var exitCode = await _buildService.BuildAsync(
                target, line => { lock (outputLines) { outputLines.Add(line); } },
                configuration: _configuration, cancellationToken: timeout.Token);
            return (exitCode, ToolReports.FormatBuildReport(exitCode, _configuration, [], outputLines) + waitNote);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (-1, $"The build was still running after {FormatDuration(_buildTimeout)} and was stopped.{waitNote}");
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
                _state.RecordFailedMergeBuild(report);
                return (true, "Not merged - your commit does not build, and the team branch must keep building.\n\n" + report);
            }

            _changedSinceBuild = false;
            _state.LastGoodCommit = head;
        }

        if (MergeReview is { } review)
        {
            var verdict = await review(_context.Name, teamHead, head, cancellationToken);
            if (!verdict.Approved)
            {
                return (true,
                    "Not merged - the lead reviewed your commit and wants changes:\n\n" + verdict.Feedback
                    + "\n\nMake them, team_commit, and team_merge again.");
            }
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

    private static string FormatDuration(TimeSpan span) =>
        span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} min" : $"{span.TotalSeconds:0} s";

    private static int CountLines(string text) => text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1;

    private static string Short(string? sha) => sha is { Length: > 8 } ? sha[..8] : sha ?? "";
}
