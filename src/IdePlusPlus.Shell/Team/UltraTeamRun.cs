using System.IO;
using System.Runtime.CompilerServices;
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
}

/// <summary>The run did not start: the solution as it stands does not build, so no worker could
/// have merged anything. Report is the build's own report.</summary>
public sealed class UltraTeamPreflightException(string report)
    : InvalidOperationException("The solution doesn't build as it stands, so the team could not merge anything. "
                                + "Fix the build, then send again.")
{
    public string Report { get; } = report;
}

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
/// jobs as the paper's runner (kickoff, idle nudges, the two deadline reminders, export).
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

    private readonly IChatClient _client;
    private readonly string _model;
    private readonly UltraTeamOptions _options;
    private readonly ReadOnlyToolDispatcher _readOnlyTools;
    private readonly GitService _gitService;
    private readonly TeamGitWorkspace _teamGit;
    private readonly TimeProvider _time;
    private readonly Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? _runCommand;
    private readonly string _runDirectory;
    private readonly Func<CancellationToken, Task<bool>>? _askToContinue;
    private int _promptTokens;
    private int _completionTokens;
    private int _cachedPromptTokens;
    private int _round = 1;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _roundStartedAt;

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
        _client = new UsageCountingChatClient(client, this);
        _askToContinue = askToContinue;
        _model = model;
        _options = options;
        _readOnlyTools = readOnlyTools;
        _gitService = gitService;
        _teamGit = teamGit;
        _time = time;
        _runCommand = options.ShellCommandsEnabled ? runCommand : null;

        RunId = $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        _runDirectory = Path.Combine(ultraRootDirectory, RunId);
        Directory.CreateDirectory(_runDirectory);

        var names = Enumerable.Range(1, Math.Max(1, options.WorkerCount)).Select(n => $"w{n}").ToList();
        Workers = names.Select(n => new TeamWorkerState(n)).ToList();
        Board = new TeamBoard(time, options.ClaimLifetime, Path.Combine(_runDirectory, "board.jsonl"));
        Bus = new TeamMessageBus(time, names);
        Board.EntryAdded += _ => OnChanged();
        Bus.MessageSent += _ => OnChanged();
    }

    public string RunId { get; }

    public TeamBoard Board { get; }

    public TeamMessageBus Bus { get; }

    public IReadOnlyList<TeamWorkerState> Workers { get; }

    /// <summary>The length of one round - the whole run, unless the user continues it.</summary>
    public TimeSpan TimeBudget => _options.TimeBudget;

    public TimeSpan Elapsed => _startedAt == default ? TimeSpan.Zero : _time.GetUtcNow() - _startedAt;

    /// <summary>1 until the user continues a run whose time ran out.</summary>
    public int Round => Volatile.Read(ref _round);

    /// <summary>What is left of the current round. Time spent waiting for the user to decide
    /// whether to continue is not taken out of the next round.</summary>
    public TimeSpan TimeLeft =>
        _roundStartedAt == default ? _options.TimeBudget : _options.TimeBudget - (_time.GetUtcNow() - _roundStartedAt);

    /// <summary>True while the run's preflight build is going, before any worker starts.</summary>
    public bool IsCheckingBuild { get; private set; }

    /// <summary>True while the run waits for the user to say whether to continue.</summary>
    public bool IsAwaitingContinue { get; private set; }

    /// <summary>Why the run is waiting to be told whether to continue - time ran out, or it stalled
    /// - in a sentence or two for the user. Set before the question is asked.</summary>
    public string? PauseExplanation { get; private set; }

    /// <summary>Set by the stall check to end the round between steps, the way running out of time does.</summary>
    private volatile string? _stalledBecause;

    private string? _repositoryRoot;
    private string? _teamBranch;

    /// <summary>The round is over for its workers: out of time, or stalled.</summary>
    private bool RoundOver => TimeLeft <= TimeSpan.Zero || _stalledBecause is not null;

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
        string? startFrom = null)
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
        }

        EnsureSucceeded(await _teamGit.CreateBranchAsync(repositoryRoot, teamBranch, start, cancellationToken), "create the team branch");

        var created = new List<(string Worktree, string Branch, string Author)>();
        var keptWorkerBranches = new List<string>();
        UltraTeamEndReason endReason;
        try
        {
            try
            {
                var solutionRelativeDirectory = Path.GetRelativePath(repositoryRoot, Path.GetDirectoryName(solutionPath)!);
                var dispatchers = new List<TeamToolDispatcher>();
                using var buildSlots = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentBuilds));

                for (var i = 0; i < Workers.Count; i++)
                {
                    var state = Workers[i];
                    var worktree = Path.Combine(_runDirectory, state.Name);
                    var branch = TeamGitWorkspace.GetWorkerBranchName(RunId, i + 1);
                    EnsureSucceeded(
                        await _teamGit.AddWorktreeAsync(repositoryRoot, worktree, branch, start, cancellationToken),
                        $"create {state.Name}'s worktree");
                    var author = $"IDE++ Ultra {state.Name}";
                    created.Add((worktree, branch, author));

                    var solutionDirectory = Path.GetFullPath(Path.Combine(worktree, solutionRelativeDirectory));
                    var context = new TeamWorkerContext(
                        state.Name,
                        repositoryRoot,
                        worktree,
                        solutionDirectory,
                        Path.Combine(solutionDirectory, Path.GetFileName(solutionPath)),
                        testProjectPaths.Select(p => Path.Combine(worktree, Path.GetRelativePath(repositoryRoot, p))).ToList(),
                        teamBranch,
                        author);
                    dispatchers.Add(new TeamToolDispatcher(
                        context, state, Board, Bus, _readOnlyTools, _gitService, _teamGit, buildSlots,
                        _options.Configuration, _runCommand));
                }

                // The one check that would have saved three whole 16-worker runs whose every merge-gate
                // build failed on a broken .NET install: one build first, and no models called at all if it
                // fails - the user gets the build's own report instead.
                if (_options.PreflightBuild)
                {
                    IsCheckingBuild = true;
                    OnChanged();
                    var (built, report) = await dispatchers[0].PreflightBuildAsync(cancellationToken);
                    IsCheckingBuild = false;
                    if (!built)
                    {
                        throw new UltraTeamPreflightException(report);
                    }
                }

                endReason = await RunWorkersAsync(goal, dispatchers, cancellationToken);
            }
            finally
            {
                // Not the run's token: a cancelled run is exactly the one whose worktrees most need
                // removing, and a stray worktree keeps its branch checked out until someone prunes it.
                foreach (var (worktree, branch, author) in created)
                {
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

                foreach (var worker in Workers.Where(w => w.Phase == TeamWorkerPhase.Working || w.Phase == TeamWorkerPhase.Waiting))
                {
                    worker.Phase = TeamWorkerPhase.Stopped;
                }

                IsCheckingBuild = false;
                OnChanged();
            }
        }
        catch (UltraTeamPreflightException)
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
    private async Task<UltraTeamEndReason> RunWorkersAsync(
        string goal, IReadOnlyList<TeamToolDispatcher> dispatchers, CancellationToken cancellationToken)
    {
        while (true)
        {
            var reason = await RunRoundAsync(goal, dispatchers, staggerStarts: Round == 1, cancellationToken);
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

    private async Task<UltraTeamEndReason> RunRoundAsync(
        string goal, IReadOnlyList<TeamToolDispatcher> dispatchers, bool staggerStarts, CancellationToken cancellationToken)
    {
        using var hardStop = new CancellationTokenSource(_options.TimeBudget + _options.HardStopGrace, _time);
        using var runToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hardStop.Token);
        using var stopReminders = CancellationTokenSource.CreateLinkedTokenSource(runToken.Token);

        var reminders = SendRemindersAsync(stopReminders.Token);
        var stallWatch = WatchForStallAsync(stopReminders.Token);
        var workers = dispatchers
            .Select((dispatcher, i) => (Dispatcher: dispatcher, State: Workers[i], Index: i))
            .Where(w => !w.State.IsDone)
            .Select(w => RunWorkerAsync(goal, w.State, w.Dispatcher, staggerStarts ? w.Index : 0, runToken.Token))
            .ToList();

        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // Reported through the end reason below.
        }
        finally
        {
            await stopReminders.CancelAsync();
            foreach (var background in new[] { reminders, stallWatch })
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

    private async Task RunWorkerAsync(
        string goal, TeamWorkerState state, TeamToolDispatcher dispatcher, int index, CancellationToken cancellationToken)
    {
        await Task.Delay(_options.StaggerDelay * index, _time, cancellationToken);
        state.Phase = TeamWorkerPhase.Working;
        OnChanged();

        var systemPrompt = TeamPrompts.WorkerSystemPrompt(
            state.Name, Workers.Select(w => w.Name).ToList(), _options.ClaimLifetime, dispatcher.Tools.Any(t => t.Name == ShellTools.RunCommandToolName));
        var loop = new HeadlessAgentLoop(
            _client,
            _model,
            _options.Effort,
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
                    goal, cycle, TimeLeft, _options.TimeBudget, previousEnding, Bus.TakeChannel(state.Name), Board.Read());
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
                    run.OnChanged();
                }

                yield return streamEvent;
            }
        }
    }
}
