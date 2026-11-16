using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Roslyn.Mutation;
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
    private readonly Func<string, int, string?, CancellationToken, Task<(bool IsError, string Text)>>? _requestHelp;
    private readonly Func<int, CancellationToken, Task<(bool IsError, string Text)>>? _waitForChange;
    private readonly Func<string, string?>? _claimGuard;
    private readonly TeamRole _role;
    private readonly HashSet<string> _offeredTools;
    private readonly BuildService _buildService = new();
    private readonly TestRunService _testRunService = new();

    /// <summary>The `dotnet format` / build process the conformity check runs. Its own instance
    /// because BuildService refuses a second invocation while one is running, and a lint check must
    /// not make this worker's ordinary build fail.</summary>
    private readonly BuildService _conformityDotnet = new();

    /// <summary>
    /// The role this worker runs as. It decides what it is offered (see <see cref="Tools"/>), what it
    /// may edit, and which board lane its findings land in. Hidden tools are not enough on their own:
    /// a model that has seen a peer edit or merge still reaches for the tool, so
    /// <see cref="ExecuteCoreAsync"/> refuses anything the role was not offered as well.
    /// </summary>
    public TeamRole Role => _role;

    /// <summary>
    /// The files the review item this specialist is on changed, set by the run when
    /// team_next_review hands it one. A <see cref="TeamRoleEditScope.ChangedFilesStyleOnly"/> role -
    /// the conformity lane - may change exactly these files and nothing else.
    /// </summary>
    public IReadOnlyList<string>? CurrentReviewFiles { get; set; }

    /// <summary>The review item this specialist is working on, for the status card and for the
    /// refusal messages. Null before its first team_next_review.</summary>
    public string? CurrentReviewId { get; set; }

    /// <summary>
    /// The line ranges the review item's merge changed, keyed by file, so run_mutation_tests mutates
    /// only what the merge touched rather than the whole file. Set by the run with the review item;
    /// null mutates every mutable construct in the file.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<LineRange>>? CurrentReviewChangedLines { get; set; }

    /// <summary>
    /// Called after a fast-forward lands on the team branch, with who merged and the merge range, so
    /// the run can queue the merge for the specialist lanes. A failure here is swallowed: the merge
    /// has already happened, and a queue that cannot enqueue must not turn a landed merge into an
    /// error the worker would try again.
    /// </summary>
    public Func<MergeLandedEvent, CancellationToken, Task>? MergeLanded { get; set; }

    /// <summary>
    /// team_next_review's implementation, answered by the run: take the oldest merge waiting for this
    /// worker's lane, sync its worktree to the team branch, and hand back the diff and the
    /// PATCH_SUMMARY. Null leaves the tool out of what the worker is offered.
    /// </summary>
    public Func<CancellationToken, Task<(bool IsError, string Text)>>? NextReview { get; set; }

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
    /// <param name="requestHelp">team_request_help's implementation - (worker, count, reason,
    /// token), answered by the run - or null when the team cannot grow, which also leaves the tool
    /// out of what the worker is offered.</param>
    /// <param name="waitForChange">team_wait's implementation - (minutes, token), answered by the run,
    /// which knows when the team changes - or null, which leaves the tool out of what the worker is offered.</param>
    /// <param name="claimGuard">Asked before a CLAIM is written, with its text: a reason to refuse it (the task
    /// is blocked, or a peer who is still working holds it) or null. Null leaves claims unchecked, as they
    /// always were.</param>
    /// <param name="role">The worker's roster role. Null is a builder - the team Ultra ran with before
    /// roles existed, so every existing caller keeps its behaviour.</param>
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
        TimeSpan? buildTimeout = null,
        Func<string, int, string?, CancellationToken, Task<(bool IsError, string Text)>>? requestHelp = null,
        Func<int, CancellationToken, Task<(bool IsError, string Text)>>? waitForChange = null,
        Func<string, string?>? claimGuard = null,
        TeamRole? role = null)
    {
        _requestHelp = requestHelp;
        _waitForChange = waitForChange;
        _claimGuard = claimGuard;
        _role = role ?? TeamRoles.Builder;
        _offeredTools = new HashSet<string>(
            TeamTools.ForWorker(
                TeamEditTool.Definition,
                shellCommandsEnabled: runCommand is not null,
                canRequestHelp: requestHelp is not null,
                canWait: waitForChange is not null,
                role: _role).Select(tool => tool.Name),
            StringComparer.Ordinal);
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
        [.. TeamTools.ForWorker(
            TeamEditTool.Definition,
            shellCommandsEnabled: _runCommand is not null,
            canRequestHelp: _requestHelp is not null,
            canWait: _waitForChange is not null,
            role: _role)
            .Where(tool => tool.Name != TeamTools.NextReviewToolName || NextReview is not null)];

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

        // Hiding a tool from the list is a prompt-level measure; this is the hard one. A model that
        // saw a peer edit or merge reaches for the same tool from muscle memory, and a role that was
        // not offered it must be told no here rather than quietly allowed.
        if (!_offeredTools.Contains(toolName))
        {
            return (true, RoleRefusal(toolName));
        }

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

            case TeamTools.RequestHelpToolName when _requestHelp is not null:
                var count = arguments?["count"] is { } countNode && int.TryParse(countNode.ToString(), out var parsed) ? parsed : 1;
                return await _requestHelp(_context.Name, count, Arg("reason"), cancellationToken);

            case TeamTools.BoardReadToolName:
                return (false, _board.Read());

            case TeamTools.WaitToolName when _waitForChange is not null:
                var minutes = arguments?["minutes"] is { } minutesNode && int.TryParse(minutesNode.ToString(), out var parsedMinutes)
                    ? parsedMinutes
                    : TeamTools.DefaultWaitMinutes;

                // Waiting is what the worker is meant to be doing, so it neither earns the idle nudge nor needs
                // something published when it ends.
                _state.HadVisibleActivityThisCycle = true;
                _state.WroteToBoardThisCycle = true;
                return await _waitForChange(minutes, cancellationToken);

            case TeamTools.BoardWriteToolName:
                return BoardWrite(Arg("type"), Arg("text"), Arg("detail"), arguments?["after"], Arg("lane"));

            case TeamTools.ReportFindingToolName:
                return ReportFinding(arguments);

            case TeamTools.NextReviewToolName:
                if (NextReview is null)
                {
                    return (true, "There is no review queue in this run - build TASKs from the board instead.");
                }

                // A review item is work the team does see, and taking one (even waiting for one) is
                // what a specialist's cycle is for, so it counts as activity either way.
                _state.HadVisibleActivityThisCycle = true;
                _state.WroteToBoardThisCycle = true;
                return await NextReview(cancellationToken);

            case TeamTools.CheckConformityToolName:
                return await CheckConformityAsync(arguments, cancellationToken);

            case TeamTools.RunMutationTestsToolName:
                return await RunMutationTestsAsync(arguments, cancellationToken);

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

    /// <summary>
    /// What this role may change, checked as a hard rule rather than left to the prompt: the
    /// specialists that write tests may not touch production code, the conformity lane may only touch
    /// the files its review item changed, and a role that reports rather than fixes may not write at
    /// all. Prompt guidance alone does not hold - a model that can see edit_file in a peer's
    /// transcript will try it.
    /// </summary>
    private string? ScopeRefusal(string fullPath, string file)
    {
        switch (_role.EditScope)
        {
            case TeamRoleEditScope.Any:
                return null;

            case TeamRoleEditScope.None:
                return $"Refused: the {_role.DisplayName} lane changes nothing - it reads and reports. Use "
                       + "report_finding (or a lane TASK) instead of editing.";

            case TeamRoleEditScope.TestsOnly:
                if (IsUnderTestProject(fullPath))
                {
                    return null;
                }

                var testProjects = _context.TestProjectPaths
                    .Select(path => Path.GetRelativePath(_context.SolutionDirectory, Path.GetDirectoryName(path) ?? path))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                return $"Refused: the {_role.DisplayName} lane writes tests only, and '{file}' is not under a test "
                       + "project. " + (testProjects.Count == 0
                           ? "This solution has no test project to add to - post a FAIL saying so."
                           : $"Put the test under {string.Join(", ", testProjects)}. Production code that looks wrong "
                             + "is a FINDING, not an edit.");

            case TeamRoleEditScope.ChangedFilesStyleOnly:
                if (CurrentReviewFiles is null)
                {
                    return $"Refused: no review item has been taken yet, so the files the {_role.DisplayName} lane may "
                           + "change are not known. Call team_next_review first, then fix only what that item changed.";
                }

                if (CurrentReviewFiles.Any(reviewed => SameFile(reviewed, fullPath)))
                {
                    return null;
                }

                return $"Refused: the {_role.DisplayName} lane changes only the files its review item changed "
                       + $"({string.Join(", ", CurrentReviewFiles)}). '{file}' is not one of them - anything wider is a "
                       + "FINDING rather than an edit.";

            default:
                return $"Refused: the {_role.DisplayName} lane may not edit '{file}'.";
        }
    }

    private bool IsUnderTestProject(string fullPath)
    {
        foreach (var project in _context.TestProjectPaths)
        {
            if (Path.GetDirectoryName(project) is not { } directory)
            {
                continue;
            }

            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Path equality for the review scope: both are full paths already, but one comes from
    /// git's diff and the other from a resolved edit, so slashes and case may differ.</summary>
    private static bool SameFile(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).Replace('/', Path.DirectorySeparatorChar),
            Path.GetFullPath(right).Replace('/', Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The message a role gets for a tool it was not offered. Naming the tool that would do
    /// the job is what turns a refusal into the next step.</summary>
    private string RoleRefusal(string toolName) => toolName switch
    {
        TeamTools.EditFileToolName when !_role.CanEdit =>
            $"Refused: no edit_file in the {_role.DisplayName} lane - it reads and reports rather than changes. "
            + "Use report_finding.",

        TeamTools.MergeToolName when !_role.CanMerge =>
            $"Refused: no team_merge in the {_role.DisplayName} lane - nothing it produces is merged. "
            + (_role.CanEdit
                ? "Commit your failing test to your own branch and quote it as evidence=branch:<sha>."
                : "Write what you found with report_finding or a lane TASK."),

        _ => $"Refused: '{toolName}' is not one of the tools the {_role.DisplayName} lane is offered. "
             + "Use the tools in your list; if the job genuinely needs this one, post a FAIL saying why.",
    };

    /// <summary>
    /// report_finding turns the fields into the board's FINDING shape, so the runner (which triages on
    /// severity and evidence) and the report (which shows severity, status and the evidence branch)
    /// never have to parse a model's prose. The text cap is the board's own.
    /// </summary>
    private (bool IsError, string Text) ReportFinding(JsonNode? arguments)
    {
        var severity = (arguments?["severity"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant() switch
        {
            "high" or "critical" or "blocker" => "high",
            "medium" or "moderate" => "medium",
            _ => "low",
        };

        var text = (arguments?["text"]?.GetValue<string>() ?? "").Trim();
        if (text.Length == 0)
        {
            return (true, "report_finding requires 'text' - what is wrong, in one or two sentences.");
        }

        var kind = (arguments?["kind"]?.GetValue<string>() ?? "general").Trim().ToLowerInvariant();
        var fixBy = (arguments?["fix_by"]?.GetValue<string>() ?? "builders").Trim().ToLowerInvariant() switch
        {
            "me" => "me",
            "user" => "user",
            _ => "builders",
        };

        var evidence = (arguments?["evidence"]?.GetValue<string>() ?? "").Trim();
        if (evidence.Length == 0)
        {
            if (severity == "high")
            {
                return (true, "severity=high needs evidence: a failing test's name, a reproducer, or branch:<sha> for a "
                              + "test you committed in your own branch. Without one this is at most medium - report it "
                              + "as medium with what you did see.");
            }

            evidence = "read-the-merge";
        }

        var files = arguments?["files"] is JsonArray array
            ? string.Join(",", array.Select(item => item?.GetValue<string>()?.Trim()).Where(item => !string.IsNullOrEmpty(item)))
            : "";

        var head = $"severity={severity} kind={kind} fix_by={fixBy} evidence={evidence}"
                   + (files.Length == 0 ? "" : $" files={files}") + " | ";

        // The board's own cap, applied here rather than left to a refusal: a finding that is too long
        // to post is a finding lost, and the fields in front are the part that is read back.
        const int cap = TeamBoard.MaxPatchSummaryLength;
        if (head.Length + text.Length > cap)
        {
            text = text[..Math.Max(0, cap - head.Length - 1)] + "…";
        }

        var (entry, error) = _board.Write(
            _context.Name, "FINDING", head + text, arguments?["detail"]?.GetValue<string>(), null, _role.NamePrefix);
        if (entry is null)
        {
            return (true, error!);
        }

        _state.HadVisibleActivityThisCycle = true;
        _state.WroteToBoardThisCycle = true;
        return (false, $"Posted {entry.Id} as a {severity} {kind} finding in the {_role.NamePrefix} lane.");
    }

    /// <summary>
    /// check_conformity runs the SDK itself - `dotnet format` and a build - rather than going through
    /// run_command, so the team's own lint never needs the user's approval. It takes one of the shared
    /// build slots, because the build it runs is a full MSBuild like any other.
    /// </summary>
    private async Task<(bool IsError, string Text)> CheckConformityAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var files = arguments?["files"] is JsonArray array
            ? array.Select(item => item?.GetValue<string>()?.Trim()).Where(item => !string.IsNullOrEmpty(item))
                .Select(item => item!).ToList()
            : [];

        if (files.Count == 0)
        {
            return (true, "check_conformity requires 'files' - the files your review item changed.");
        }

        var checker = new TeamConformityChecker(
            _context.SolutionDirectory,
            _context.SolutionPath,
            _configuration,
            runDotnet: RunDotnetForConformityAsync,
            diagnostics: (file, token) => _readOnlyTools.ExecuteAsync(
                DiagnosticTools.GetDiagnosticsToolName,
                new JsonObject { ["file"] = file },
                _context.SolutionDirectory,
                token));

        var report = await checker.CheckAsync(files, cancellationToken);
        if (report.Refused)
        {
            return (true, report.Text);
        }

        _state.HadVisibleActivityThisCycle = true;
        return (false, report.Text);
    }

    private async Task<DotnetRunResult> RunDotnetForConformityAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        await _buildSlots.WaitAsync(cancellationToken);
        try
        {
            var output = new List<string>();
            var exitCode = await _conformityDotnet.RunDotnetAsync(
                arguments,
                workingDirectory,
                line => { lock (output) { output.Add(line); } },
                cancellationToken);
            return new DotnetRunResult(exitCode, string.Join('\n', output));
        }
        finally
        {
            _buildSlots.Release();
        }
    }

    /// <summary>
    /// run_mutation_tests, the mutation tester's lane: mutate the changed lines of one file and see
    /// which mutants the tests kill. It takes the shared build slots itself (each mutant is a build
    /// and a test run), and it accepts the review item's changed lines so only what the merge touched
    /// is mutated - a whole-file run on a large class would spend its whole budget on code the merge
    /// never went near.
    /// </summary>
    private async Task<(bool IsError, string Text)> RunMutationTestsAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var file = arguments?["file"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(file))
        {
            return (true, "run_mutation_tests requires a 'file'.");
        }

        var maxMutants = arguments?["max_mutants"] is { } maxNode && int.TryParse(maxNode.ToString(), out var parsedMax)
            ? parsedMax
            : TeamTools.DefaultMutantsPerRun;
        var filter = arguments?["test_filter"]?.GetValue<string>();

        var runner = new TeamMutationRunner(
            new MutationRunnerContext(
                _context.WorktreeRoot,
                _context.SolutionDirectory,
                _context.TestProjectPaths,
                _configuration),
            _buildSlots);

        var (isError, text) = await runner.RunAsync(
            file.Trim(), maxMutants, filter, ChangedLinesFor(file.Trim()), cancellationToken);
        if (!isError)
        {
            _state.HadVisibleActivityThisCycle = true;
        }

        return (isError, text);
    }

    /// <summary>The review item's changed line ranges for one file, or null to mutate the whole file.
    /// The keys are the paths the merge named, which may differ in slashes from the tool argument.</summary>
    private IReadOnlyList<LineRange>? ChangedLinesFor(string file)
    {
        if (CurrentReviewChangedLines is null)
        {
            return null;
        }

        var normalized = file.Replace('\\', '/');
        foreach (var (path, ranges) in CurrentReviewChangedLines)
        {
            if (string.Equals(path.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith('/' + path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                return ranges;
            }
        }

        return null;
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

        if (ScopeRefusal(fullPath, file) is { } scopeRefusal)
        {
            return (true, scopeRefusal);
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

    private (bool IsError, string Text) BoardWrite(string? type, string? text, string? detail, JsonNode? after, string? lane)
    {
        // A claim is the one entry that is a decision about who does what, so it is the one that can be
        // refused for that: a task that cannot start yet, or that a peer who is still at it holds.
        if (_claimGuard is not null && TeamBoard.TryParseType(type, out var parsedType)
            && parsedType == TeamBoardEntryType.Claim && _claimGuard(text ?? "") is { } refusal)
        {
            return (true, refusal);
        }

        // A specialist's finding is that lane's business, so it carries the lane tag without the model
        // having to remember. Everything else - a TASK for the builders, a FACT for everyone - is
        // tagged only when the caller says so, since the gap finder's TASKs are the builders' work.
        var effectiveLane = TeamBoard.NormalizeLane(lane)
                            ?? (TeamBoard.TryParseType(type, out var entryType)
                                && entryType == TeamBoardEntryType.Finding
                                && _role.IsSpecialist
                                    ? _role.NamePrefix
                                    : null);

        var (entry, error) = _board.Write(_context.Name, type, text, detail, TeamBoard.ParseTaskIds(after), effectiveLane);
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

        if (MergeLanded is { } onMergeLanded)
        {
            try
            {
                await onMergeLanded(new MergeLandedEvent(_context.Name, teamHead, head), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The merge has landed and cannot be taken back. A queue that fails to record it costs
                // the specialist lanes one review item, which is not worth turning the worker's step
                // into an error it would retry - a retry would be refused as "nothing to merge" anyway.
            }
        }

        return (false, $"Merged {Short(head)} into the team branch. Post a PATCH_SUMMARY for it.");
    }

    private async Task<(bool IsError, string Text)> DoneAsync(string? summary, CancellationToken cancellationToken)
    {
        // Only a role that merges can be told to merge first. The adversary's whole product is a
        // failing test committed on its own branch and quoted as evidence - there is deliberately
        // nothing of its to bring onto the team branch, so asking it to merge would loop forever.
        if (_role.CanMerge && !_warnedAboutUnmergedWork && await DescribeUnmergedWorkAsync(cancellationToken) is { } unmerged)
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
