namespace IdePlusPlus.Shell.Team;

/// <summary>Where one Ultra worker works. Every path is inside the worker's own worktree except
/// <see cref="RepositoryRoot"/>, the user's checkout - worktrees share its refs, which is what lets
/// a worker move the team branch, but nothing a worker runs uses it as a working directory.</summary>
public sealed record TeamWorkerContext(
    string Name,
    string RepositoryRoot,
    string WorktreeRoot,
    string SolutionDirectory,
    string SolutionPath,
    IReadOnlyList<string> TestProjectPaths,
    string TeamBranch,
    string AuthorName);

public enum TeamWorkerPhase
{
    Waiting,
    Working,
    Done,
    Stopped,
}

/// <summary>
/// What a worker has done so far - written by that worker's own dispatcher and coordinator loop,
/// read by the run's status card. Fields are only ever set from the worker's own flow, so plain
/// volatile reads are enough for the UI to show something recent; nothing decides anything on a
/// torn read of two of them together.
/// </summary>
/// <param name="time">The clock a step is stamped with. The run's own, so a test that drives time sees
/// workers go quiet.</param>
public sealed class TeamWorkerState(string name, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private long _lastStepTicks;
    private volatile string? _currentClaim;
    private volatile string? _lastTool;
    private volatile string? _doneSummary;
    private volatile string? _lastGoodCommit;
    private volatile int _phase = (int)TeamWorkerPhase.Waiting;
    private int _steps;
    private int _merges;
    private int _cycles;

    public string Name { get; } = name;

    public TeamWorkerPhase Phase
    {
        get => (TeamWorkerPhase)_phase;
        set => _phase = (int)value;
    }

    public string? CurrentClaim { get => _currentClaim; set => _currentClaim = value; }

    public string? LastTool { get => _lastTool; set => _lastTool = value; }

    public string? DoneSummary { get => _doneSummary; set => _doneSummary = value; }

    /// <summary>The newest commit known to build - set by a passing build of a clean worktree, or
    /// by committing exactly what last built. team_merge builds again only when HEAD is not this.</summary>
    public string? LastGoodCommit { get => _lastGoodCommit; set => _lastGoodCommit = value; }

    public int Steps => Volatile.Read(ref _steps);

    public int Merges => Volatile.Read(ref _merges);

    public int Cycles => Volatile.Read(ref _cycles);

    /// <summary>Set when this cycle merged, posted, or wrote to the board - a cycle that ends with
    /// none of those gets the idle nudge rather than just a fresh start.</summary>
    public bool HadVisibleActivityThisCycle { get; set; }

    /// <summary>Set when this cycle wrote a board entry. One that did other work but recorded none
    /// of it gets the publish reminder: peers see the board, never a worker's conversation.</summary>
    public bool WroteToBoardThisCycle { get; set; }

    public bool IsDone => Phase == TeamWorkerPhase.Done;

    /// <summary>When the worker last called a tool, or null before its first step.</summary>
    public DateTimeOffset? LastStepAt =>
        Volatile.Read(ref _lastStepTicks) is var ticks and > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

    /// <summary>
    /// Still working on something: in the run, and not quiet for longer than <paramref name="within"/>.
    /// A worker waiting for a peer's merge (team_wait) or reading the board counts - it is taking steps. What
    /// does not count is one that finished, stopped, or has not been heard from for that long. This, not the
    /// age of its claim, is what says whether it can be taken over from: a claim lapses on a timer
    /// whether or not its worker is still at it, and a worker waiting on a prerequisite let its claim lapse
    /// while a peer, reading that as "stalled", redid its work.
    /// </summary>
    public bool IsActive(TimeSpan within) =>
        Phase == TeamWorkerPhase.Working && (LastStepAt is not { } last || _time.GetUtcNow() - last < within);

    internal void RecordStep(string tool)
    {
        Interlocked.Increment(ref _steps);
        LastTool = tool;
        Volatile.Write(ref _lastStepTicks, _time.GetUtcNow().UtcTicks);
    }

    internal void RecordMerge() => Interlocked.Increment(ref _merges);

    private int _failedMergeBuilds;
    private volatile string? _lastMergeBuildFailure;

    /// <summary>team_merge refusals because the commit did not build - what the runner's stall check
    /// counts, since a gate that never passes is what keeps a whole team from merging.</summary>
    public int FailedMergeBuilds => Volatile.Read(ref _failedMergeBuilds);

    /// <summary>The start of the latest failed merge build's report.</summary>
    public string? LastMergeBuildFailure => _lastMergeBuildFailure;

    internal void RecordFailedMergeBuild(string report)
    {
        _lastMergeBuildFailure = report;
        Interlocked.Increment(ref _failedMergeBuilds);
    }

    internal int StartCycle()
    {
        HadVisibleActivityThisCycle = false;
        WroteToBoardThisCycle = false;
        return Interlocked.Increment(ref _cycles);
    }
}
