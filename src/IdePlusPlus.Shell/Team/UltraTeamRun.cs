using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Team;

public sealed record UltraTeamOptions(
    int WorkerCount,
    TimeSpan TimeBudget,
    bool ShellCommandsEnabled,
    string Configuration,
    ChatEffort Effort)
{
    /// <summary>How long after one worker starts the next does. Agensh staggers starts so the first
    /// workers can put claims on the board before everyone picks at once.</summary>
    public TimeSpan StaggerDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>However big the team, starts are spread over no more than this - at five seconds
    /// apart a 64-worker team's last worker would otherwise wait over five minutes.</summary>
    public TimeSpan MaxTotalStagger { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How many model requests the whole team may have in flight at once; the rest wait
    /// their turn. A big team otherwise sends every worker's request at the same moment, which a
    /// provider answers with rate limits and a local model server with a queue it cannot hold.</summary>
    public int MaxConcurrentModelRequests { get; init; } = 16;

    /// <summary>A request refused for rate limiting (or an overloaded provider) is retried this many
    /// times, backing off from <see cref="RateLimitBackoff"/>, before the worker sees the error -
    /// three such errors in a row stop a worker, and a burst can hit many workers at once.</summary>
    public int RateLimitRetries { get; init; } = 5;

    public TimeSpan RateLimitBackoff { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The run ends once its workers have used this many tokens between them (prompt plus
    /// completion), whatever time is left; 0 means no limit. The team is told at 90% to stop starting
    /// new work, the way the time reminders tell it. Set for a run left to go until done, which has
    /// nobody to ask whether to keep spending.</summary>
    public long MaxTokens { get; init; }

    /// <summary>Auto mode: the team starts with <see cref="InitialWorkerCount"/> workers, and its
    /// workers may ask for more (team_request_help) when the work left will not fit the time left -
    /// up to WorkerCount, at most two at a time, no sooner than <see cref="HelpCooldown"/> after the
    /// last time it grew, and not once less than <see cref="MinTimeLeftForHelp"/> is left. Off, the
    /// team is WorkerCount from the start, as before.</summary>
    public bool AutoScale { get; init; }

    public int InitialWorkerCount { get; init; } = 2;

    public TimeSpan HelpCooldown { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>A newcomer needs time to read in, build and merge before it is any use; less than
    /// this left (or a quarter of the round, if that is shorter) and help is refused.</summary>
    public TimeSpan MinTimeLeftForHelp { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Free bytes on the drive holding the given path - the real drive unless a test says
    /// otherwise.</summary>
    public Func<string, long>? FreeDiskSpace { get; init; }

    /// <summary>How long workers get to finish their current step once time is up, before they are
    /// cancelled outright.</summary>
    public TimeSpan HardStopGrace { get; init; } = TimeSpan.FromMinutes(2);

    public TimeSpan ClaimLifetime { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxIterationsPerCycle { get; init; } = 40;

    /// <summary>A cycle whose history grows past this many characters (about 75k tokens) is ended
    /// so the next starts fresh from the board - there is no compaction to fall back on.</summary>
    public int MaxCycleHistoryChars { get; init; } = 300_000;

    public int MaxConcurrentBuilds { get; init; } = 2;

    /// <summary>Build the solution once before any worker starts, and refuse to run if it fails -
    /// every merge needs a passing build, so a solution that cannot build cannot take any of the
    /// team's work. On by default; tests that never merge turn it off.</summary>
    public bool PreflightBuild { get; init; } = true;

    /// <summary>A round is stalled when nothing has reached the team branch for this long and at
    /// least <see cref="StallFailedMergeBuilds"/> merge builds have failed meanwhile. It is then
    /// paused and the user asked, rather than left to spend the rest of its budget on a gate that
    /// will not pass.</summary>
    public TimeSpan StallWindow { get; init; } = TimeSpan.FromMinutes(10);

    public int StallFailedMergeBuilds { get; init; } = 3;

    /// <summary>How often the stall check looks. Short only in tests.</summary>
    public TimeSpan StallCheckInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Run with a lead: it plans the work onto the board before any worker starts, checks in
    /// on the team, and reviews each merge before it lands. Costs the lead's own model calls on top of
    /// the workers', and serialises merges behind its review.</summary>
    public bool Orchestrated { get; init; }

    /// <summary>The client (provider) workers use, when it should differ from the run's own. Null: the same.</summary>
    public IChatClient? WorkerClient { get; init; }

    /// <summary>The model workers use, when it should differ from the run's own. Null: the same.</summary>
    public string? WorkerModel { get; init; }

    /// <summary>The effort workers use, when it should differ from <see cref="Effort"/>. Null: the same.</summary>
    public ChatEffort? WorkerEffort { get; init; }

    /// <summary>The lead's client (provider). Null: the run's own.</summary>
    public IChatClient? OrchestratorClient { get; init; }

    /// <summary>The lead's model. Null: the run's own.</summary>
    public string? OrchestratorModel { get; init; }

    public ChatEffort OrchestratorEffort { get; init; } = ChatEffort.High;

    /// <summary>How often the lead checks in on the team while it works.</summary>
    public TimeSpan SuperviseInterval { get; init; } = TimeSpan.FromMinutes(4);

    /// <summary>How many times in a row the lead may turn down one worker's merge before it lands anyway.</summary>
    public int MaxConsecutiveRejections { get; init; } = 2;
}

/// <summary>The run did not start: the solution as it stands does not build, so no worker could
/// have merged anything. Report is the build's own report.</summary>
/// <param name="notMirrored">Projects outside the repository that the team's copy could not be given,
/// so a failure may be down to them rather than to the solution - the user's own tree can build while
/// the copy cannot.</param>
public sealed class UltraTeamPreflightException(string report, IReadOnlyList<string>? notMirrored = null)
    : InvalidOperationException("The solution doesn't build as it stands, so the team could not merge anything. "
                                + "Fix the build, then send again.")
{
    public string Report { get; } = report;

    public IReadOnlyList<string> NotMirrored { get; } = notMirrored ?? [];
}

/// <summary>The run did not start, or stopped before any worker did: the team's worktrees would not
/// fit on the disk. Every worktree is a full checkout plus its own build output, so a big team on a
/// big solution adds up quickly.</summary>
public sealed class UltraTeamDiskSpaceException(string message) : InvalidOperationException(message);

public enum UltraTeamEndReason
{
    AllDone,
    TimeUp,
    Cancelled,

    /// <summary>Every worker stopped without calling team_done - failing requests, or a model that
    /// stopped calling tools.</summary>
    WorkersStopped,

    /// <summary>Paused because nothing could merge: no work reached the team branch for the stall
    /// window while merge builds kept failing.</summary>
    Stalled,

    /// <summary>The team used up its token budget (<see cref="UltraTeamOptions.MaxTokens"/>).</summary>
    BudgetSpent,
}

/// <summary>What a finished run leaves behind. The team branch is kept (when anything was merged)
/// until the result is applied or discarded. Worker worktrees are gone; a worker's branch is gone
/// too unless it held work the team branch does not - those are listed in KeptWorkerBranches.</summary>
public sealed record UltraTeamResult(
    string RunId,
    string RepositoryRoot,
    string SnapshotCommit,
    string TeamBranch,
    bool TeamBranchKept,
    IReadOnlyList<TeamFileChange> Changes,
    UltraTeamEndReason EndReason,
    IReadOnlyList<TeamWorkerState> Workers,
    IReadOnlyList<TeamBoardEntry> Board,
    int PromptTokens,
    int CompletionTokens,
    IReadOnlyList<string> KeptWorkerBranches);

/// <summary>
/// Runs one Ultra team: snapshots the user's working tree, gives each worker its own worktree and
/// branch off it, runs every worker's cycles in parallel until the goal is met or time is up, then
/// removes the worktrees and reports what reached the team branch.
///
/// This is the "runner" from Agensh, not an orchestrator: it never decides who does what. It opens
/// the run, keeps each worker cycling, says when time is short, and closes the run - the same four
/// jobs as the paper's runner (kickoff, idle nudges, the two deadline reminders, export). When
/// <see cref="UltraTeamOptions.Orchestrated"/> is set, a separate <see cref="UltraOrchestrator"/> - the
/// lead - does the deciding instead: it plans onto the board, checks in on the workers and reviews
/// each merge. The runner's own jobs are unchanged either way.
///
/// Each cycle is a fresh conversation - system prompt plus a user message carrying the goal, the
/// time left, new channel posts and the board. A worker's knowledge crosses cycles only via the
/// board and the code it merged, which is the paper's design and also means a worker's context
/// never grows past one cycle's worth.
/// </summary>
public sealed class UltraTeamRun
{
    private const int MaxConsecutiveIdleCycles = 3;

    private const string UnfinishedWorkCommitMessage =
        "Uncommitted work when the Ultra run ended - kept so it is not lost; unverified";

    private readonly IChatClient _workerClient;
    private readonly IChatClient _orchestratorClient;
    private readonly string _model;
    private readonly UltraTeamOptions _options;
    private readonly ReadOnlyToolDispatcher _readOnlyTools;
    private readonly GitService _gitService;
    private readonly TeamGitWorkspace _teamGit;
    private readonly TimeProvider _time;
    private readonly Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? _runCommand;
    private readonly string _runDirectory;
    private readonly string _ultraRootDirectory;
    private readonly Func<CancellationToken, Task<bool>>? _askToContinue;
    private UltraOrchestrator? _orchestrator;
    private int _promptTokens;
    private int _completionTokens;
    private int _cachedPromptTokens;
    private int _round = 1;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _roundStartedAt;

    /// <summary>Guards the team itself - workers, their dispatchers and worktrees, and the current
    /// round's worker tasks - which auto mode adds to from a worker's tool call mid-round.</summary>
    private readonly object _teamLock = new();
    private readonly List<TeamWorkerState> _workers = [];
    private readonly List<TeamToolDispatcher> _dispatchers = [];
    private readonly List<(string Worktree, string Branch, string Author)> _worktrees = [];
    private List<Task>? _roundTasks;
    private CancellationToken _roundToken;

    /// <summary>One growth at a time, and when the last one was.</summary>
    private readonly SemaphoreSlim _growing = new(1, 1);
    private DateTimeOffset _lastGrowth = DateTimeOffset.MinValue;

    // Set once RunAsync knows them, for creating a worker - at the start, or when the team grows.
    private string _goal = "";
    private string _solutionPath = "";
    private string _solutionRelativeDirectory = "";
    private IReadOnlyList<string> _testProjectPaths = [];
    private TeamExternalFolders _externalFolders = TeamExternalFolders.None;
    private IReadOnlyList<string> _ignoredDependencyFolders = [];

    /// <summary>What the team's copy lacks that it could not be given, for the failure message. Guarded
    /// by <see cref="_teamLock"/>: a worker auto mode adds mid-run can add to it.</summary>
    private readonly List<string> _notMirrored = [];

    /// <summary>The dependency links made inside each worktree, to be removed before the worktree is.</summary>
    private readonly Dictionary<string, IReadOnlyList<string>> _dependencyLinks = [];
    private SemaphoreSlim? _buildSlots;

    /// <summary>What one worktree takes on disk: a checkout before anything is built in it, and a
    /// guess at it with build output until the preflight build measures the real thing.</summary>
    private long _checkoutBytes;
    private long _worktreeBytes;

    /// <summary>Kept free beyond what the worktrees need, for everything else on the machine.</summary>
    private const long DiskHeadroomBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>A worktree's size with build output, as a multiple of its checkout, until one is
    /// measured: generous, since running out of disk mid-run is worse than refusing at the start.</summary>
    private const int BuildOutputFactor = 4;

    /// <param name="ultraRootDirectory">Where run folders go - outside any repository, so worktrees
    /// never show up in the user's status, file list or index.</param>
    /// <param name="runCommand">run_command for workers, or null. Only used when the options allow
    /// shell commands.</param>
    /// <param name="askToContinue">Asked when a round's time runs out: true starts another round of
    /// the same length with the same workers, worktrees and board; false (or null here) ends the run.
    /// Nothing is spent while it waits - every worker has already stopped between steps.</param>
    public UltraTeamRun(
        IChatClient client,
        string model,
        UltraTeamOptions options,
        ReadOnlyToolDispatcher readOnlyTools,
        GitService gitService,
        TeamGitWorkspace teamGit,
        TimeProvider time,
        string ultraRootDirectory,
        Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? runCommand,
        Func<CancellationToken, Task<bool>>? askToContinue = null)
    {
        // One throttle per provider: each has its own rate limits, and the run's usage counts them all.
        var wrapped = new Dictionary<IChatClient, IChatClient>(ReferenceEqualityComparer.Instance);
        IChatClient Wrap(IChatClient inner) =>
            wrapped.TryGetValue(inner, out var existing)
                ? existing
                : wrapped[inner] = new UsageCountingChatClient(new ThrottlingChatClient(inner, options, time), this);
        _workerClient = Wrap(options.WorkerClient ?? client);
        _orchestratorClient = Wrap(options.OrchestratorClient ?? client);
        _askToContinue = askToContinue;
        _model = model;
        _options = options;
        _readOnlyTools = readOnlyTools;
        _gitService = gitService;
        _teamGit = teamGit;
        _time = time;
        _runCommand = options.ShellCommandsEnabled ? runCommand : null;

        RunId = $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        _ultraRootDirectory = ultraRootDirectory;
        _runDirectory = Path.Combine(ultraRootDirectory, RunId);
        Directory.CreateDirectory(_runDirectory);

        var startingWorkers = options.AutoScale
            ? Math.Clamp(options.InitialWorkerCount, 1, Math.Max(1, options.WorkerCount))
            : Math.Max(1, options.WorkerCount);
        var names = Enumerable.Range(1, startingWorkers).Select(n => $"w{n}").ToList();
        _workers.AddRange(names.Select(n => new TeamWorkerState(n)));
        Board = new TeamBoard(time, options.ClaimLifetime, Path.Combine(_runDirectory, "board.jsonl"));
        Bus = new TeamMessageBus(time, options.Orchestrated ? [.. names, LeadTools.LeadName] : names);
        Board.EntryAdded += _ => OnChanged();
        Bus.MessageSent += _ => OnChanged();
    }

    public string RunId { get; }

    public TeamBoard Board { get; }

    /// <summary>The lead, for an orchestrated run once it has started - null otherwise.</summary>
    public UltraOrchestrator? Orchestrator => _orchestrator;

    public TeamMessageBus Bus { get; }

    /// <summary>The team so far, in the order its workers joined. Fixed for an ordinary run; in auto
    /// mode it grows while the run goes on, so this is a copy of the moment it was read.</summary>
    public IReadOnlyList<TeamWorkerState> Workers
    {
        get
        {
            lock (_teamLock)
            {
                return [.. _workers];
            }
        }
    }

    /// <summary>The team as it is whenever it is read - for the lead, which holds on to it for the
    /// whole run and so must see the workers auto mode adds, where <see cref="Workers"/> is a copy.</summary>
    private sealed class LiveTeam(UltraTeamRun run) : IReadOnlyList<TeamWorkerState>
    {
        public TeamWorkerState this[int index] => run.Workers[index];

        public int Count => run.Workers.Count;

        public IEnumerator<TeamWorkerState> GetEnumerator() => run.Workers.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>How big the team may get - its size from the start, unless it is in auto mode.</summary>
    public int MaxWorkers => _options.AutoScale ? Math.Max(1, _options.WorkerCount) : Workers.Count;

    public bool IsAutoScaling => _options.AutoScale;

    /// <summary>The length of one round - the whole run, unless the user continues it.</summary>
    public TimeSpan TimeBudget => _options.TimeBudget;

    public TimeSpan Elapsed => _startedAt == default ? TimeSpan.Zero : _time.GetUtcNow() - _startedAt;

    /// <summary>1 until the user continues a run whose time ran out.</summary>
    public int Round => Volatile.Read(ref _round);

    /// <summary>What is left of the current round. Time spent waiting for the user to decide
    /// whether to continue is not taken out of the next round.</summary>
    public TimeSpan TimeLeft =>
        _roundStartedAt == default ? _options.TimeBudget : _options.TimeBudget - (_time.GetUtcNow() - _roundStartedAt);

    /// <summary>How many board entries this run took over from the earlier run it carries on from.</summary>
    public int CarriedBoardEntries { get; private set; }

    /// <summary>True while the run's preflight build is going, before any worker starts.</summary>
    public bool IsCheckingBuild { get; private set; }

    /// <summary>True while the run waits for the user to say whether to continue.</summary>
    public bool IsAwaitingContinue { get; private set; }

    /// <summary>Why the run is waiting to be told whether to continue - time ran out, or it stalled
    /// - in a sentence or two for the user. Set before the question is asked.</summary>
    public string? PauseExplanation { get; private set; }

    /// <summary>Set by the stall check to end the round between steps, the way running out of time does.</summary>
    private volatile string? _stalledBecause;

    /// <summary>Set once the token budget is spent; unlike a stall it is never cleared - no later
    /// round gets more budget.</summary>
    private volatile string? _budgetSpentBecause;

    private int _budgetWarningSent;

    private string? _repositoryRoot;
    private string? _teamBranch;

    /// <summary>The round is over for its workers: out of time or token budget, or stalled.</summary>
    private bool RoundOver => TimeLeft <= TimeSpan.Zero || _stalledBecause is not null || _budgetSpentBecause is not null;

    /// <summary>The token budget the run ends at, or 0 for none.</summary>
    public long MaxTokens => _options.MaxTokens;

    /// <summary>The branch this run carries on from, merged into the working-tree snapshot it starts
    /// at - null for a run that starts from the working tree alone.</summary>
    public string? StartedFrom { get; private set; }

    public int PromptTokens => Volatile.Read(ref _promptTokens);

    public int CompletionTokens => Volatile.Read(ref _completionTokens);

    /// <summary>The part of <see cref="PromptTokens"/> providers served from their prompt cache.</summary>
    public int CachedPromptTokens => Volatile.Read(ref _cachedPromptTokens);

    /// <summary>Something a status display would show has changed. Raised on worker threads, often -
    /// a subscriber that touches UI should marshal and throttle.</summary>
    public event Action? Changed;

    /// <summary>The two reminders' offsets from the start: "stop starting new work" at 80% of the
    /// budget, then "merge now" two minutes before the end - or at 90% when the budget is short
    /// enough that two minutes would come first.</summary>
    internal static (TimeSpan Deadline, TimeSpan Final) ReminderSchedule(TimeSpan budget) =>
        (budget * 0.8, budget - TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(2).Ticks, (budget * 0.1).Ticks)));

    /// <param name="startFrom">A branch to carry on from - an earlier run's team branch, or any branch
    /// or commit - merged into the working-tree snapshot so workers start with both. Refused, before
    /// anything is created, if it conflicts with the working tree.</param>
    public async Task<UltraTeamResult> RunAsync(
        string goal,
        string repositoryRoot,
        string solutionPath,
        IReadOnlyList<string> testProjectPaths,
        CancellationToken cancellationToken,
        string? startFrom = null,
        IReadOnlyList<string>? backlog = null)
    {
        _startedAt = _time.GetUtcNow();
        _roundStartedAt = _startedAt;
        var teamBranch = TeamGitWorkspace.GetTeamBranchName(RunId);
        _repositoryRoot = repositoryRoot;
        _teamBranch = teamBranch;

        // The snapshot stays what the result is compared against - the user's tree as it was - so a
        // run started from an earlier branch offers that branch's work for review too, not only what
        // this run added on top of it.
        var snapshot = await _teamGit.CreateSnapshotCommitAsync(repositoryRoot, cancellationToken);
        var start = snapshot;
        if (!string.IsNullOrWhiteSpace(startFrom))
        {
            startFrom = startFrom.Trim();
            var merge = await _teamGit.MergeIntoSnapshotAsync(repositoryRoot, snapshot, startFrom, cancellationToken);
            if (merge.Status == TeamStartMergeStatus.Conflicted)
            {
                throw new InvalidOperationException(
                    $"Can't start from {startFrom}: it changes the same lines as your working tree in "
                    + $"{string.Join(", ", merge.ConflictedPaths)}. Commit, stash or undo those edits - or apply "
                    + "that branch first - and send again.");
            }

            start = merge.Commit!;
            StartedFrom = startFrom;
            goal = goal + "\n\n" + TeamPrompts.ContinuingFrom(startFrom);
            CarryBoardFrom(startFrom);
        }

        SeedBacklog(backlog);

        // Folders beside the repository that the solution builds from. Worktrees live in the run folder,
        // where "..\RRS.FamRetail" points at nothing, so they are linked in below; the team is told they
        // are there, and that they are not theirs to change.
        _externalFolders = TeamExternalProjects.Find(solutionPath, repositoryRoot);
        if (_externalFolders.Siblings.Count > 0)
        {
            goal = goal + "\n\n" + TeamPrompts.LinkedFolders(_externalFolders.Siblings.Select(f => f.Name).ToList());
        }

        // Dependency folders git ignores (node_modules) are not in a checkout either, and a project can
        // build from them: each worktree gets a link to the user's own.
        _ignoredDependencyFolders = await _teamGit.FindIgnoredDependencyFoldersAsync(repositoryRoot, cancellationToken);
        if (_ignoredDependencyFolders.Count > 0)
        {
            goal = goal + "\n\n" + TeamPrompts.LinkedDependencyFolders(_ignoredDependencyFolders);
        }

        _goal = goal;
        _solutionPath = solutionPath;
        _solutionRelativeDirectory = Path.GetRelativePath(repositoryRoot, Path.GetDirectoryName(solutionPath)!);
        _testProjectPaths = testProjectPaths;

        // Before anything exists: a team whose worktrees would not fit is refused while there is
        // nothing to clean up. A guess - the preflight below measures a real worktree.
        _checkoutBytes = await _teamGit.GetTreeSizeAsync(repositoryRoot, start, cancellationToken);
        _worktreeBytes = _checkoutBytes * BuildOutputFactor;
        EnsureDiskSpace(Workers.Count, _worktreeBytes, "the team's worktrees");

        if (_options.Orchestrated)
        {
            _orchestrator = new UltraOrchestrator(
                _orchestratorClient,
                _options.OrchestratorModel ?? _model,
                _options.OrchestratorEffort,
                _readOnlyTools,
                Path.GetDirectoryName(solutionPath)!,
                Board,
                Bus,
                new LiveTeam(this),
                _time,
                _options.MaxConsecutiveRejections);
            _orchestrator.Changed += OnChanged;
        }

        EnsureSucceeded(await _teamGit.CreateBranchAsync(repositoryRoot, teamBranch, start, cancellationToken), "create the team branch");

        var keptWorkerBranches = new List<string>();
        IReadOnlyList<string> externalLinks = [];
        UltraTeamEndReason endReason;
        try
        {
            try
            {
                // Before the first worktree is built in. A link that cannot be made does not stop the
                // run here: the preflight build then fails the way it would have, and says which folders
                // the copy was missing.
                lock (_teamLock)
                {
                    _notMirrored.AddRange(_externalFolders.Unreachable);
                }

                try
                {
                    externalLinks = TeamExternalProjects.Link(_runDirectory, _externalFolders.Siblings);
                }
                catch (InvalidOperationException ex)
                {
                    lock (_teamLock)
                    {
                        _notMirrored.AddRange(_externalFolders.Siblings.Select(f => $"{f.SourcePath} ({ex.Message})"));
                    }
                }

                using var buildSlots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentBuilds));
                _buildSlots = buildSlots;
                foreach (var state in Workers)
                {
                    await CreateWorkerAsync(state, start, cancellationToken);
                }

                // The one check that would have saved three whole 16-worker runs whose every merge-gate
                // build failed on a broken .NET install: one build first, and no models called at all if it
                // fails - the user gets the build's own report instead.
                if (_options.PreflightBuild)
                {
                    IsCheckingBuild = true;
                    OnChanged();
                    TeamToolDispatcher first;
                    lock (_teamLock)
                    {
                        first = _dispatchers[0];
                    }

                    var (built, report) = await first.PreflightBuildAsync(cancellationToken);
                    IsCheckingBuild = false;
                    if (!built)
                    {
                        throw new UltraTeamPreflightException(report, NotInTheCopy(report, repositoryRoot));
                    }

                    // A built worktree is the real size; every other one will grow to it as it builds.
                    _worktreeBytes = Math.Max(_checkoutBytes, DirectorySize(Path.Combine(_runDirectory, Workers[0].Name)));
                    EnsureDiskSpace(Workers.Count - 1, Math.Max(0, _worktreeBytes - _checkoutBytes), "the team's builds");
                }

                if (_orchestrator is { } planner)
                {
                    await planner.PlanAsync(goal, cancellationToken);
                }

                endReason = await RunWorkersAsync(goal, cancellationToken);
            }
            finally
            {
                // Not the run's token: a cancelled run is exactly the one whose worktrees most need
                // removing, and a stray worktree keeps its branch checked out until someone prunes it.
                List<(string Worktree, string Branch, string Author)> created;
                lock (_teamLock)
                {
                    created = [.. _worktrees];
                }

                foreach (var (worktree, branch, author) in created)
                {
                    // First of all: git removes a worktree by deleting what is in it, and a link to the
                    // user's node_modules is something it could follow into the real folder.
                    UnlinkDependencies(worktree);
                    var keep = await PreserveUnmergedWorkAsync(repositoryRoot, worktree, author, teamBranch);
                    await _teamGit.RemoveWorktreeAsync(repositoryRoot, worktree, CancellationToken.None);
                    if (keep)
                    {
                        keptWorkerBranches.Add(branch);
                    }
                    else
                    {
                        await _teamGit.DeleteBranchAsync(repositoryRoot, branch, CancellationToken.None);
                    }
                }

                // After the worktrees: nothing builds against the links once those are gone. Only the
                // links go - the folders they point at are the user's.
                TeamExternalProjects.Unlink(externalLinks);

                foreach (var worker in Workers.Where(w => w.Phase == TeamWorkerPhase.Working || w.Phase == TeamWorkerPhase.Waiting))
                {
                    worker.Phase = TeamWorkerPhase.Stopped;
                }

                IsCheckingBuild = false;
                _buildSlots = null;
                OnChanged();
            }
        }
        catch (Exception ex) when (ex is UltraTeamPreflightException or UltraTeamDiskSpaceException)
        {
            // Nothing was done, so nothing is kept: the team branch only marks where it would have started.
            await _teamGit.DeleteBranchAsync(repositoryRoot, teamBranch, CancellationToken.None);
            throw;
        }

        var changes = await _teamGit.GetChangedFilesAsync(repositoryRoot, snapshot, teamBranch, CancellationToken.None);
        var keepBranch = changes.Count > 0;
        if (!keepBranch)
        {
            await _teamGit.DeleteBranchAsync(repositoryRoot, teamBranch, CancellationToken.None);
        }

        return new UltraTeamResult(
            RunId, repositoryRoot, snapshot, teamBranch, keepBranch, changes, endReason,
            Workers, Board.Snapshot(), PromptTokens, CompletionTokens, keptWorkerBranches);
    }

    /// <summary>
    /// Gives <paramref name="state"/> its worktree at <paramref name="startCommit"/>, its branch and
    /// its dispatcher, and adds it to the team - for the workers a run starts with, and for each one
    /// auto mode adds later.
    /// </summary>
    private async Task<TeamToolDispatcher> CreateWorkerAsync(TeamWorkerState state, string startCommit, CancellationToken cancellationToken)
    {
        var repositoryRoot = _repositoryRoot!;
        var worktree = Path.Combine(_runDirectory, state.Name);
        var branch = TeamGitWorkspace.GetWorkerBranchName(RunId, int.Parse(state.Name[1..]));
        EnsureSucceeded(
            await _teamGit.AddWorktreeAsync(repositoryRoot, worktree, branch, startCommit, cancellationToken),
            $"create {state.Name}'s worktree");
        await LinkDependenciesAsync(worktree, repositoryRoot, cancellationToken);
        var author = $"IDE++ Ultra {state.Name}";

        var solutionDirectory = Path.GetFullPath(Path.Combine(worktree, _solutionRelativeDirectory));
        var context = new TeamWorkerContext(
            state.Name,
            repositoryRoot,
            worktree,
            solutionDirectory,
            Path.Combine(solutionDirectory, Path.GetFileName(_solutionPath)),
            _testProjectPaths.Select(p => Path.Combine(worktree, Path.GetRelativePath(repositoryRoot, p))).ToList(),
            _teamBranch!,
            author);
        var dispatcher = new TeamToolDispatcher(
            context, state, Board, Bus, _readOnlyTools, _gitService, _teamGit, _buildSlots!,
            _options.Configuration, _runCommand,
            requestHelp: _options.AutoScale ? RequestHelpAsync : null);

        // Here rather than once the team is created, so the lead also reviews the merges of
        // workers auto mode adds later.
        if (_orchestrator is { } lead)
        {
            dispatcher.MergeReview = async (worker, teamHead, head, ct) =>
            {
                string diff;
                try
                {
                    diff = await _teamGit.GetDiffTextAsync(
                        repositoryRoot, teamHead, head, UltraOrchestrator.MaxReviewDiffChars, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return (true, ""); // Nothing to review against; the build gate already passed.
                }

                return await lead.ReviewMergeAsync(_goal, worker, diff, ct);
            };
        }

        lock (_teamLock)
        {
            _worktrees.Add((worktree, branch, author));
            if (!_workers.Contains(state))
            {
                _workers.Add(state);
            }

            _dispatchers.Add(dispatcher);
        }

        return dispatcher;
    }

    /// <summary>
    /// Links the user's git-ignored dependency folders into <paramref name="worktree"/> at the same
    /// relative paths. A link that cannot be made, or that git would not ignore in the worktree (so a
    /// worker's `git add -A` would stage it), is removed again and reported in the failure message
    /// instead; the worker is created either way, and the preflight build says whether it mattered.
    /// </summary>
    private async Task LinkDependenciesAsync(string worktree, string repositoryRoot, CancellationToken cancellationToken)
    {
        var links = new List<string>();
        foreach (var folder in _ignoredDependencyFolders)
        {
            var relative = folder.Replace('/', Path.DirectorySeparatorChar);
            var link = Path.Combine(worktree, relative);
            if (Path.Exists(link))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                TeamExternalProjects.CreateLink(link, Path.Combine(repositoryRoot, relative));
                links.Add(link);
                if (!await _teamGit.IsIgnoredAsync(worktree, folder, cancellationToken))
                {
                    TeamExternalProjects.Unlink([link]);
                    links.Remove(link);
                    lock (_teamLock)
                    {
                        _notMirrored.Add($"{relative} (git would not ignore a link to it in the team's copy)");
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                lock (_teamLock)
                {
                    _notMirrored.Add($"{relative} ({ex.Message})");
                }
            }
        }

        lock (_teamLock)
        {
            _dependencyLinks[worktree] = links;
        }
    }

    private void UnlinkDependencies(string worktree)
    {
        IReadOnlyList<string>? links;
        lock (_teamLock)
        {
            _dependencyLinks.Remove(worktree, out links);
        }

        if (links is not null)
        {
            TeamExternalProjects.Unlink(links);
        }
    }

    /// <summary>What a failed preflight build says the team's copy lacks: what could not be linked in,
    /// and whatever the report names that is in the user's tree but not the copy - the git-ignored
    /// folders (generated sources, local config) a build needs and a checkout never has.</summary>
    private IReadOnlyList<string> NotInTheCopy(string report, string repositoryRoot)
    {
        List<string> known;
        lock (_teamLock)
        {
            known = [.. _notMirrored];
        }

        var firstWorktree = Path.Combine(_runDirectory, Workers[0].Name);
        foreach (var missing in TeamBuildReport.FindMissingFromCopy(report, firstWorktree, repositoryRoot))
        {
            known.Add($"{missing} (in your tree, but not in the team's copy - git ignores it)");
        }

        return known;
    }

    /// <summary>
    /// team_request_help: auto mode's answer to a worker who judges the work left will not fit the
    /// time left. The worker decides it needs help; the runner decides whether it gets it, and refuses
    /// - saying why, so the worker plans with the team it has - when the team is at its limit, grew
    /// too recently for newcomers to have started, has too little time or token budget left for a
    /// newcomer to be any use, or the disk will not take another worktree.
    ///
    /// Newcomers start from the team branch as it is now, not where the run began - they get what
    /// the team has merged - and join the current round at once.
    /// </summary>
    private async Task<(bool IsError, string Text)> RequestHelpAsync(
        string requester, int requested, string? reason, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return (true, "Say which work the new workers should take, and why the team as it is cannot finish it in time.");
        }

        await _growing.WaitAsync(cancellationToken);
        try
        {
            var size = Workers.Count;
            var now = _time.GetUtcNow();
            var minTimeLeft = TimeSpan.FromTicks(Math.Min(_options.MinTimeLeftForHelp.Ticks, _options.TimeBudget.Ticks / 4));
            if (size >= MaxWorkers)
            {
                return (true, $"Refused: the team is at its limit of {MaxWorkers} workers. Share out the remaining work "
                              + "among the team as it is - put it on the board as TASKs.");
            }

            if (now - _lastGrowth < _options.HelpCooldown)
            {
                var wait = _options.HelpCooldown - (now - _lastGrowth);
                return (true, $"Refused for now: the team grew {(int)(now - _lastGrowth).TotalSeconds}s ago and the newcomers "
                              + $"are still starting. If it is still needed, ask again in {(int)Math.Ceiling(wait.TotalSeconds)}s.");
            }

            if (RoundOver || TimeLeft < minTimeLeft)
            {
                return (true, "Refused: too little time is left for a newcomer to read in, build and merge anything. "
                              + "Finish with the team as it is.");
            }

            if (_options.MaxTokens > 0 && (long)PromptTokens + CompletionTokens >= _options.MaxTokens * 3 / 4)
            {
                return (true, "Refused: most of the token budget is spent, and every newcomer spends it faster. Finish "
                              + "with the team as it is.");
            }

            var grant = Math.Min(Math.Clamp(requested, 1, TeamTools.MaxWorkersPerHelpRequest), MaxWorkers - size);
            if (DiskShortfall(grant, _worktreeBytes) is { } shortfall)
            {
                return (true, $"Refused: the disk will not take {grant} more worktree(s) - {shortfall}. Finish with the "
                              + "team as it is.");
            }

            var teamHead = await _teamGit.ResolveRefAsync(_repositoryRoot!, _teamBranch!, cancellationToken)
                           ?? throw new InvalidOperationException("The team branch is gone.");
            var joined = new List<string>();
            for (var i = 0; i < grant; i++)
            {
                var state = new TeamWorkerState($"w{size + i + 1}");
                var dispatcher = await CreateWorkerAsync(state, teamHead, cancellationToken);
                Bus.AddMember(state.Name);
                joined.Add(state.Name);

                // The requester is still in this round, so the round is not over and cannot end until
                // this returns.
                CancellationToken roundToken;
                lock (_teamLock)
                {
                    roundToken = _roundToken;
                }

                var task = RunWorkerAsync(_goal, state, dispatcher, 0, roundToken);
                lock (_teamLock)
                {
                    _roundTasks?.Add(task);
                }
            }

            _lastGrowth = _time.GetUtcNow();
            var names = string.Join(", ", joined);
            Bus.Post(TeamMessageBus.RunnerName, $"{names} joined the team at {requester}'s request: {Cap(reason.Trim(), 300)}");
            OnChanged();
            return (false, $"Granted: {names} {(joined.Count == 1 ? "is" : "are")} joining now, from the team branch as it "
                           + "is. Newcomers read the board first - make sure the work you want them on is there as TASKs. "
                           + $"The team is now {Workers.Count} of up to {MaxWorkers}.");
        }
        finally
        {
            _growing.Release();
        }
    }

    /// <summary>Refuses to go on when <paramref name="worktrees"/> more of <paramref name="bytesEach"/>
    /// would leave less than the headroom free.</summary>
    private void EnsureDiskSpace(int worktrees, long bytesEach, string what)
    {
        if (DiskShortfall(worktrees, bytesEach) is { } shortfall)
        {
            throw new UltraTeamDiskSpaceException(
                $"There isn't room on the disk for {what}: {shortfall}. Use fewer workers, or free some space.");
        }
    }

    private string? DiskShortfall(int worktrees, long bytesEach)
    {
        if (worktrees <= 0)
        {
            return null;
        }

        var needed = worktrees * bytesEach;
        var free = (_options.FreeDiskSpace ?? DriveFreeSpace)(_runDirectory);
        return free - needed >= DiskHeadroomBytes
            ? null
            : $"{worktrees} worktree(s) need about {Gigabytes(needed)}, {Gigabytes(free)} is free, and "
              + $"{Gigabytes(DiskHeadroomBytes)} is kept spare";
    }

    private static long DriveFreeSpace(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    private static string Gigabytes(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";

    /// <summary>A built worktree's size; anything unreadable is skipped - it is an estimate.</summary>
    private static long DirectorySize(string directory)
    {
        try
        {
            return new DirectoryInfo(directory)
                // Links are skipped, not followed: a linked node_modules is the user's, and shared, so it is
                // none of this worktree's weight - and following it would count it once per worker.
                .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(file => file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>
    /// When the run carries on from an earlier run's team branch, takes over what that run learned:
    /// its FACTs, FAILs and PATCH_SUMMARYs, and the TASKs it did not finish. Its claims are left
    /// behind - they belonged to workers that no longer exist. A branch from anywhere else has no
    /// board, and nothing is carried.
    /// </summary>
    private void CarryBoardFrom(string branch)
    {
        var match = System.Text.RegularExpressions.Regex.Match(branch, @"^(?:refs/heads/)?ideplusplus/ultra/(?<run>[^/]+)/team$");
        if (!match.Success)
        {
            return;
        }

        var earlierRun = match.Groups["run"].Value;
        var entries = TeamBoard.ReadJournal(Path.Combine(_ultraRootDirectory, earlierRun, "board.jsonl"));
        var carried = entries
            .Where(e => e.Type is TeamBoardEntryType.Fact or TeamBoardEntryType.Fail or TeamBoardEntryType.PatchSummary)
            .Concat(TeamBoard.UnfinishedTasks(entries))
            .OrderBy(e => e.Number)
            .ToList();
        Board.Import(carried, $"run {earlierRun}");
        CarriedBoardEntries = carried.Count;
    }

    /// <summary>Puts each step of an approved plan on the board as a TASK, so workers take the
    /// plan's pieces instead of each surveying the codebase and choosing overlapping work.</summary>
    private void SeedBacklog(IReadOnlyList<string>? backlog)
    {
        foreach (var step in backlog ?? [])
        {
            var text = step.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            Board.Write(
                TeamMessageBus.RunnerName,
                "TASK",
                text.Length <= TeamBoard.MaxPatchSummaryLength ? text : text[..(TeamBoard.MaxPatchSummaryLength - 3)] + "...",
                text.Length <= TeamBoard.MaxPatchSummaryLength ? null : text);
        }
    }

    /// <summary>
    /// Whether a worker's branch holds work the team branch does not, after first committing any
    /// edits it left uncommitted. Deleting every worker branch at the end used to throw away whatever
    /// had not been merged - and on a run whose merge gate never passed, that was all of it, with only
    /// git's unreachable objects left to recover it from.
    /// </summary>
    private async Task<bool> PreserveUnmergedWorkAsync(string repositoryRoot, string worktree, string author, string teamBranch)
    {
        try
        {
            if (await _teamGit.HasUncommittedChangesAsync(worktree, CancellationToken.None))
            {
                await _teamGit.CommitAllAsync(worktree, UnfinishedWorkCommitMessage, author, CancellationToken.None);
            }

            var head = await _teamGit.GetHeadAsync(worktree, CancellationToken.None);
            return !await _teamGit.IsAncestorAsync(repositoryRoot, head, teamBranch, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Could not tell. Keeping a branch costs nothing; deleting one that held work loses it.
            return true;
        }
    }

    /// <summary>
    /// Runs rounds until one ends for any reason but time - or time runs out and the user does not
    /// want another. A new round restarts every worker that has not called team_done, from a fresh
    /// cycle: the worktrees, branches and board are untouched in between, so nothing is lost but the
    /// conversation each worker was in, which the board already carries across cycles anyway.
    /// </summary>
    private async Task<UltraTeamEndReason> RunWorkersAsync(string goal, CancellationToken cancellationToken)
    {
        while (true)
        {
            var reason = await RunRoundAsync(goal, staggerStarts: Round == 1, cancellationToken);
            if (reason is not (UltraTeamEndReason.TimeUp or UltraTeamEndReason.Stalled) || _askToContinue is null)
            {
                return reason;
            }

            PauseExplanation = reason == UltraTeamEndReason.Stalled
                ? _stalledBecause
                : "Time is up.";

            bool another;
            IsAwaitingContinue = true;
            OnChanged();
            try
            {
                another = await _askToContinue(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return UltraTeamEndReason.Cancelled;
            }
            finally
            {
                IsAwaitingContinue = false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return UltraTeamEndReason.Cancelled;
            }

            if (!another)
            {
                return reason;
            }

            Interlocked.Increment(ref _round);
            _roundStartedAt = _time.GetUtcNow();
            _stalledBecause = null;
            PauseExplanation = null;
            OnChanged();
        }
    }

    private async Task<UltraTeamEndReason> RunRoundAsync(string goal, bool staggerStarts, CancellationToken cancellationToken)
    {
        using var hardStop = new CancellationTokenSource(_options.TimeBudget + _options.HardStopGrace, _time);
        using var runToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardStop.Token);
        using var stopReminders = CancellationTokenSource.CreateLinkedTokenSource(runToken.Token);

        var reminders = SendRemindersAsync(stopReminders.Token);
        var stallWatch = WatchForStallAsync(stopReminders.Token);
        var supervision = _orchestrator is { } lead
            ? lead.SuperviseAsync(goal, () => TimeLeft, _options.SuperviseInterval, stopReminders.Token)
            : Task.CompletedTask;
        List<(TeamWorkerState State, TeamToolDispatcher Dispatcher, int Index)> starting;
        lock (_teamLock)
        {
            _roundToken = runToken.Token;
            _roundTasks = [];
            starting = _workers
                .Select((state, i) => (State: state, Dispatcher: _dispatchers[i], Index: i))
                .Where(w => !w.State.IsDone)
                .ToList();
        }

        // Started outside the lock - a worker runs until its first await before handing back - and
        // registered one by one, so a newcomer added meanwhile is never lost.
        foreach (var (state, dispatcher, index) in starting)
        {
            var task = RunWorkerAsync(goal, state, dispatcher, staggerStarts ? index : 0, runToken.Token);
            lock (_teamLock)
            {
                _roundTasks!.Add(task);
            }
        }

        try
        {
            // Until no worker is left running - including any that joined while the others worked.
            while (true)
            {
                Task[] running;
                lock (_teamLock)
                {
                    running = [.. _roundTasks!];
                }

                try
                {
                    await Task.WhenAll(running);
                }
                catch (OperationCanceledException)
                {
                    // Reported through the end reason below.
                }

                lock (_teamLock)
                {
                    if (_roundTasks!.Count == running.Length)
                    {
                        _roundTasks = null;
                        break;
                    }
                }
            }
        }
        finally
        {
            lock (_teamLock)
            {
                _roundTasks = null;
            }

            await stopReminders.CancelAsync();
            foreach (var background in new[] { reminders, stallWatch, supervision })
            {
                try
                {
                    await background;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        return cancellationToken.IsCancellationRequested ? UltraTeamEndReason.Cancelled
            : Workers.All(w => w.IsDone) ? UltraTeamEndReason.AllDone
            : _budgetSpentBecause is not null ? UltraTeamEndReason.BudgetSpent
            : _stalledBecause is not null ? UltraTeamEndReason.Stalled
            : TimeLeft <= TimeSpan.Zero ? UltraTeamEndReason.TimeUp
            : UltraTeamEndReason.WorkersStopped;
    }

    /// <summary>
    /// Ends the round when the team has stopped getting anywhere: nothing has reached the team branch
    /// for the stall window, and merge builds have kept failing over it. Both, not either - a long
    /// quiet stretch with no failed builds is a team working on something big, and failed builds that
    /// still let others merge are ordinary work. It is the pattern three FAM runs showed for their
    /// whole budget, every merge build failing the same way on a broken machine.
    /// </summary>
    private async Task WatchForStallAsync(CancellationToken cancellationToken)
    {
        var lastHead = await _teamGit.ResolveRefAsync(_repositoryRoot!, _teamBranch!, cancellationToken);
        var lastMovedAt = _time.GetUtcNow();
        var failuresAtLastMove = Workers.Sum(w => w.FailedMergeBuilds);

        while (true)
        {
            await Task.Delay(_options.StallCheckInterval, _time, cancellationToken);
            var head = await _teamGit.ResolveRefAsync(_repositoryRoot!, _teamBranch!, cancellationToken);
            var failures = Workers.Sum(w => w.FailedMergeBuilds);
            if (head != lastHead)
            {
                lastHead = head;
                lastMovedAt = _time.GetUtcNow();
                failuresAtLastMove = failures;
                continue;
            }

            var quietFor = _time.GetUtcNow() - lastMovedAt;
            var failedSince = failures - failuresAtLastMove;
            if (quietFor < _options.StallWindow || failedSince < _options.StallFailedMergeBuilds)
            {
                continue;
            }

            var latest = Workers.Select(w => w.LastMergeBuildFailure).LastOrDefault(f => f is not null) ?? "";
            _stalledBecause =
                $"The team has stalled: nothing has reached the team branch for {(int)quietFor.TotalMinutes} min, and "
                + $"{failedSince} merge builds failed in that time. The latest failure:\n{Cap(latest, 600)}";
            Bus.Broadcast(TeamMessageBus.RunnerName, TeamPrompts.StallNotice);
            OnChanged();
            return;
        }
    }

    private static string Cap(string text, int max) => text.Length <= max ? text : text[..max] + " [...]";

    /// <summary>The gap between one worker's start and the next: the configured delay, shrunk so the
    /// last of <paramref name="workers"/> starts within <paramref name="maxTotal"/>.</summary>
    internal static TimeSpan StaggerStep(TimeSpan delay, TimeSpan maxTotal, int workers) =>
        workers <= 1 ? TimeSpan.Zero : TimeSpan.FromTicks(Math.Min(delay.Ticks, maxTotal.Ticks / (workers - 1)));

    private async Task RunWorkerAsync(
        string goal, TeamWorkerState state, TeamToolDispatcher dispatcher, int index, CancellationToken cancellationToken)
    {
        await Task.Delay(StaggerStep(_options.StaggerDelay, _options.MaxTotalStagger, Workers.Count) * index, _time, cancellationToken);
        state.Phase = TeamWorkerPhase.Working;
        OnChanged();

        var systemPrompt = TeamPrompts.WorkerSystemPrompt(
            state.Name,
            Workers.Select(w => w.Name).ToList(),
            _options.ClaimLifetime,
            dispatcher.Tools.Any(t => t.Name == ShellTools.RunCommandToolName),
            maxTeamSize: _options.AutoScale ? MaxWorkers : 0,
            orchestrated: _options.Orchestrated);
        var loop = new HeadlessAgentLoop(
            _workerClient,
            _options.WorkerModel ?? _model,
            _options.WorkerEffort ?? _options.Effort,
            dispatcher.Tools,
            async (tool, arguments, ct) =>
            {
                var result = await dispatcher.ExecuteAsync(tool, arguments, ct);
                OnChanged();
                return result;
            },
            dispatcher.DrainNotices);

        string? previousEnding = null;
        var nudge = (string?)null;
        var idleCycles = 0;

        try
        {
            while (!state.IsDone && !RoundOver)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cycle = state.StartCycle();
                var opening = TeamPrompts.CycleStart(
                    goal, cycle, TimeLeft, _options.TimeBudget, previousEnding, Bus.TakeChannel(state.Name), Board.CycleDigest(state.Name));
                if (nudge is not null)
                {
                    opening = $"{nudge}\n\n{opening}";
                }

                var history = new List<ChatMessage> { new(ChatRole.System, systemPrompt), new(ChatRole.User, opening) };
                var result = await loop.RunAsync(
                    history,
                    _options.MaxIterationsPerCycle,
                    cancellationToken,
                    stopBeforeNextIteration: () =>
                        state.IsDone || RoundOver || HistoryChars(history) > _options.MaxCycleHistoryChars);

                if (result.StoppedEarly && !state.IsDone && !RoundOver)
                {
                    // Ended for context size: one short wrap-up so what the worker learned this cycle
                    // reaches the board instead of vanishing with the history.
                    history.Add(new ChatMessage(ChatRole.User, TeamPrompts.ContextFullNotice));
                    result = await loop.RunAsync(history, 3, cancellationToken);
                }

                previousEnding = result.FinalText;
                idleCycles = result.ToolCallCount == 0 ? idleCycles + 1 : 0;
                if (idleCycles >= MaxConsecutiveIdleCycles)
                {
                    // A model that answers in prose and never calls a tool would otherwise cycle until
                    // the deadline, spending tokens on nothing.
                    Bus.Post(state.Name, $"{state.Name} stopped: {MaxConsecutiveIdleCycles} cycles in a row without a tool call.");
                    break;
                }

                nudge = !state.HadVisibleActivityThisCycle ? TeamPrompts.IdleNudge
                    : !state.WroteToBoardThisCycle ? TeamPrompts.PublishReminder
                    : null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One worker's failure (a provider error, a git process that would not start) must not
            // take the team down with it; peers see why it went quiet.
            state.LastTool = $"error: {ex.Message}";
            Bus.Post(state.Name, $"{state.Name} stopped after an error: {ex.Message}");
        }
        finally
        {
            if (!state.IsDone)
            {
                state.Phase = TeamWorkerPhase.Stopped;
                Board.ReleaseClaims(state.Name);
            }

            OnChanged();
        }
    }

    private async Task SendRemindersAsync(CancellationToken cancellationToken)
    {
        var (deadline, final) = ReminderSchedule(_options.TimeBudget);

        await Task.Delay(deadline, _time, cancellationToken);
        Bus.Broadcast(TeamMessageBus.RunnerName, TeamPrompts.DeadlineReminder(TimeLeft));

        var untilFinal = final - (_options.TimeBudget - TimeLeft);
        if (untilFinal > TimeSpan.Zero)
        {
            await Task.Delay(untilFinal, _time, cancellationToken);
        }

        Bus.Broadcast(TeamMessageBus.RunnerName, TeamPrompts.FinalReminder(TimeLeft));
    }

    private static int HistoryChars(List<ChatMessage> history) =>
        history.Sum(m => m.Content.Length + (m.ToolCalls?.Sum(c => c.ArgumentsJson.Length) ?? 0));

    private static void EnsureSucceeded(GitCommandResult result, string what)
    {
        if (!result.Success)
        {
            throw new InvalidOperationException($"Could not {what}: {result.Output}");
        }
    }

    private void OnChanged() => Changed?.Invoke();

    /// <summary>Called as usage arrives: warns the team once at 90% of the token budget, and ends the
    /// round at 100%. Workers stop between steps, as they do when time runs out.</summary>
    private void CheckTokenBudget()
    {
        if (_options.MaxTokens <= 0 || _budgetSpentBecause is not null)
        {
            return;
        }

        var used = (long)PromptTokens + CompletionTokens;
        if (used >= _options.MaxTokens)
        {
            _budgetSpentBecause = $"The token budget is spent: {used:N0} of {_options.MaxTokens:N0} tokens used.";
            OnChanged();
        }
        else if (used >= _options.MaxTokens * 0.9 && Interlocked.Exchange(ref _budgetWarningSent, 1) == 0)
        {
            Bus.Broadcast(TeamMessageBus.RunnerName, TeamPrompts.BudgetReminder);
        }
    }

    /// <summary>
    /// The team's share of the provider: at most <see cref="UltraTeamOptions.MaxConcurrentModelRequests"/>
    /// requests in flight, and a request refused for rate limiting retried with backoff instead of
    /// handed to the worker as a failed step.
    ///
    /// A retry is only safe because the refusal is the stream's first and only event - nothing has
    /// reached the worker yet. The slot is given back while backing off, so one rate-limited worker
    /// does not hold up the others that might get through.
    /// </summary>
    private sealed class ThrottlingChatClient(IChatClient inner, UltraTeamOptions options, TimeProvider time) : IChatClient
    {
        private static readonly Regex RateLimited = new(@"\b(429|503|529)\b|rate.?limit|overloaded", RegexOptions.IgnoreCase);

        private readonly SemaphoreSlim _slots = new(Math.Max(1, options.MaxConcurrentModelRequests));

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition>? tools = null,
            ChatEffort effort = ChatEffort.None,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var attempt = 0; ; attempt++)
            {
                await _slots.WaitAsync(cancellationToken);
                var released = false;
                try
                {
                    await using var events = inner.StreamChatAsync(messages, model, tools, effort, cancellationToken)
                        .GetAsyncEnumerator(cancellationToken);
                    if (!await events.MoveNextAsync())
                    {
                        yield break;
                    }

                    if (events.Current is StreamError error && RateLimited.IsMatch(error.Message) && attempt < options.RateLimitRetries)
                    {
                        _slots.Release();
                        released = true;
                        await Task.Delay(Backoff(attempt), time, cancellationToken);
                        continue;
                    }

                    yield return events.Current;
                    while (await events.MoveNextAsync())
                    {
                        yield return events.Current;
                    }

                    yield break;
                }
                finally
                {
                    if (!released)
                    {
                        _slots.Release();
                    }
                }
            }
        }

        /// <summary>Doubling from the configured base, capped at a minute, with jitter so workers
        /// refused together do not all come back together.</summary>
        private TimeSpan Backoff(int attempt) =>
            TimeSpan.FromMilliseconds(
                Math.Min(60_000, options.RateLimitBackoff.TotalMilliseconds * Math.Pow(2, attempt))
                * (0.75 + Random.Shared.NextDouble() * 0.5));
    }

    /// <summary>Adds each response's usage to the run's totals as it arrives, so the status card's
    /// token count moves during a cycle rather than only when one finishes.</summary>
    private sealed class UsageCountingChatClient(IChatClient inner, UltraTeamRun run) : IChatClient
    {
        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition>? tools = null,
            ChatEffort effort = ChatEffort.None,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var streamEvent in inner.StreamChatAsync(messages, model, tools, effort, cancellationToken))
            {
                if (streamEvent is UsageReceived usage)
                {
                    Interlocked.Add(ref run._promptTokens, usage.PromptTokens);
                    Interlocked.Add(ref run._completionTokens, usage.CompletionTokens);
                    Interlocked.Add(ref run._cachedPromptTokens, usage.CachedPromptTokens);
                    run.CheckTokenBudget();
                    run.OnChanged();
                }

                yield return streamEvent;
            }
        }
    }
}
