using IdePlusPlus.Copilot.Core.Team;

namespace IdePlusPlus.Shell.Team;

/// <summary>What became of one merge, for one review lane.</summary>
public enum ReviewItemStatus
{
    /// <summary>Nobody in the lane has looked at it yet.</summary>
    Pending,

    /// <summary>A specialist took it with team_next_review and is working on it.</summary>
    Taken,

    /// <summary>The specialist finished - whether or not it found anything. A finding is a board
    /// entry, not a state of the queue.</summary>
    Done,
}

/// <summary>
/// One merge, waiting to be reviewed by the lanes <see cref="Lanes"/> names. The item is shared by
/// every lane it was queued for - they each keep their own status - so the diff stat, the author and
/// the PATCH_SUMMARY are stored once.
///
/// Read through the queue that owns it: <see cref="StatusFor"/> and the lane list are snapshots taken
/// under the queue's lock, since a specialist marks them from its own thread.
/// </summary>
public sealed class ReviewItem
{
    private readonly List<TeamRoleKind> _lanes = [];
    private readonly Dictionary<TeamRoleKind, ReviewItemStatus> _statuses = [];

    internal ReviewItem(int id, MergeLanding landing, IReadOnlyList<TeamRoleKind> lanes)
    {
        Id = id;
        FromSha = landing.FromSha;
        ToSha = landing.ToSha;
        Author = landing.Author;
        TaskId = landing.TaskId;
        Generation = landing.Generation;
        Files = landing.Files;
        PatchSummary = landing.PatchSummary;
        FixesFinding = landing.FixesFinding;
        IsPeriodic = landing.IsPeriodic;
        foreach (var lane in lanes)
        {
            _lanes.Add(lane);
            _statuses[lane] = ReviewItemStatus.Pending;
        }
    }

    /// <summary>1-based, in the order the merges landed.</summary>
    public int Id { get; }

    public string FromSha { get; }

    public string ToSha { get; }

    /// <summary>The worker whose merge this was.</summary>
    public string Author { get; }

    /// <summary>The TASK the merge's PATCH_SUMMARY named, as the board ids it ("G12"), or null when the
    /// summary named none - which the report shows, since an unlabelled merge is work nobody accounted
    /// for.</summary>
    public string? TaskId { get; }

    /// <summary>1 for an ordinary merge. A merge that fixes a FINDING starts the next generation, and
    /// anything at 2 or more goes only to the conformity lane - see the loop guard on the queue.</summary>
    public int Generation { get; }

    public IReadOnlyList<string> Files { get; }

    public string? PatchSummary { get; }

    /// <summary>The FINDING this merge claims to fix, or null.</summary>
    public string? FixesFinding { get; }

    /// <summary>A periodic item: the gap finder's and the scout's look at everything merged so far,
    /// with no diff of its own. Also what wrap-up queues.</summary>
    public bool IsPeriodic { get; }

    public IReadOnlyList<TeamRoleKind> Lanes
    {
        get
        {
            lock (_statuses)
            {
                return [.. _lanes];
            }
        }
    }

    public ReviewItemStatus StatusFor(TeamRoleKind lane)
    {
        lock (_statuses)
        {
            return _statuses.GetValueOrDefault(lane, ReviewItemStatus.Done);
        }
    }

    /// <summary>Whether every lane this item was queued for has finished with it.</summary>
    public bool IsDone
    {
        get
        {
            lock (_statuses)
            {
                return _statuses.Values.All(status => status == ReviewItemStatus.Done);
            }
        }
    }

    internal bool TryTake(TeamRoleKind lane)
    {
        lock (_statuses)
        {
            if (!_statuses.TryGetValue(lane, out var status) || status != ReviewItemStatus.Pending)
            {
                return false;
            }

            _statuses[lane] = ReviewItemStatus.Taken;
            return true;
        }
    }

    internal void MarkDone(TeamRoleKind lane)
    {
        lock (_statuses)
        {
            if (_statuses.ContainsKey(lane))
            {
                _statuses[lane] = ReviewItemStatus.Done;
            }
        }
    }

    internal void Release(TeamRoleKind lane)
    {
        lock (_statuses)
        {
            if (_statuses.TryGetValue(lane, out var status) && status == ReviewItemStatus.Taken)
            {
                _statuses[lane] = ReviewItemStatus.Pending;
            }
        }
    }
}

/// <summary>
/// A merge that has landed on the team branch, as the queue needs it: what changed, who merged it,
/// which TASK it finished, and - when it is a fix - which finding it answers. The run builds one of
/// these in its merge callback; the queue only stores it.
/// </summary>
public sealed record MergeLanding(
    string FromSha,
    string ToSha,
    string Author,
    IReadOnlyList<string> Files,
    int Generation = 1,
    string? TaskId = null,
    string? PatchSummary = null,
    string? FixesFinding = null,
    bool IsPeriodic = false);

/// <summary>
/// The post-merge queue: one item per merge, offered to each quality lane the composition has, so
/// the specialists keep working on merged code while the builders carry on with the next TASK.
/// Before this, a specialist could only look at the team branch when its own cycle happened to
/// start, and the builders could only find out what was wrong by being told.
///
/// Two things stop the queue from turning into a spiral. The first is the <b>loop guard</b>: a merge
/// that fixes a FINDING is queued at the next generation, and an item at generation 2 or more goes
/// only to the conformity lane, which fixes style and reports behaviour rather than filing new
/// findings - so a finding cannot breed a finding forever. The second is <see cref="Close"/>: after
/// the run's 80% deadline reminder the queue takes nothing new, so a specialist's cycle after that
/// drains what is queued and stops.
///
/// The gap finder and the follow-up scout do not work on single diffs, so they get a periodic item
/// every <see cref="PeriodicInterval"/> merges and one at wrap-up instead of one per merge.
///
/// Thread-safe: the runner enqueues on the merging worker's thread while specialists take and
/// complete from theirs.
/// </summary>
public sealed class TeamReviewQueue
{
    /// <summary>How many merges between the goal-level lanes' (gap finder, follow-up scout) looks
    /// at the code.</summary>
    public const int PeriodicInterval = 3;

    /// <summary>Generation at which an item stops going to every lane and goes only to conformity.
    /// See the loop guard on the type.</summary>
    public const int LoopGuardGeneration = 2;

    private static readonly TeamRoleKind[] EveryMergeLanes =
        [TeamRoleKind.MutationTester, TeamRoleKind.Adversary, TeamRoleKind.Conformity];

    private static readonly TeamRoleKind[] PeriodicLanes =
        [TeamRoleKind.GapFinder, TeamRoleKind.FollowUpScout];

    private readonly object _lock = new();
    private readonly List<ReviewItem> _items = [];
    private readonly TimeProvider _time;
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextId = 1;
    private int _merges;

    /// <param name="lanes">The specialist lanes this run has, from its composition. A lane that is not
    /// here never gets an item, which is how a Speed run queues nothing at all.</param>
    /// <param name="time">The run's clock, so a test can drive the wait.</param>
    public TeamReviewQueue(IEnumerable<TeamRoleKind> lanes, TimeProvider? time = null)
    {
        var configured = lanes.Where(lane => lane != TeamRoleKind.Builder).Distinct().ToList();
        EveryMergeLanesOfThisRun = configured.Intersect(EveryMergeLanes).ToList();
        PeriodicLanesOfThisRun = configured.Intersect(PeriodicLanes).ToList();
        _time = time ?? TimeProvider.System;
    }

    private IReadOnlyList<TeamRoleKind> EveryMergeLanesOfThisRun { get; }

    private IReadOnlyList<TeamRoleKind> PeriodicLanesOfThisRun { get; }

    /// <summary>Whether the queue takes new items. False once the deadline reminder has passed, or
    /// once the run is wrapping up.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>How many merges have been queued. The gap finder's interval counts these.</summary>
    public int MergeCount
    {
        get
        {
            lock (_lock)
            {
                return _merges;
            }
        }
    }

    /// <summary>Every item, oldest first.</summary>
    public IReadOnlyList<ReviewItem> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>True when no lane has an item left: nothing pending, nothing taken. Wrap-up waits for
    /// this.</summary>
    public bool Drained
    {
        get
        {
            lock (_lock)
            {
                return _items.All(item => item.IsDone);
            }
        }
    }

    /// <summary>The lanes this queue offers items to at all - so a specialist can be told it has
    /// nothing to do in this run.</summary>
    public IReadOnlyList<TeamRoleKind> Lanes => [.. EveryMergeLanesOfThisRun, .. PeriodicLanesOfThisRun];

    /// <summary>
    /// Queues a merge for every lane it concerns, or returns null when no lane wants it: the queue is
    /// closed, the run has no specialist lanes, or the item belongs to the generation the loop guard
    /// keeps away from every lane but conformity. The caller keeps merging either way.
    /// </summary>
    public ReviewItem? Enqueue(MergeLanding landing)
    {
        ReviewItem? item;
        lock (_lock)
        {
            if (IsClosed)
            {
                return null;
            }

            var lanes = landing.IsPeriodic
                ? PeriodicLanesOfThisRun.ToList()
                : landing.Generation >= LoopGuardGeneration
                    ? EveryMergeLanesOfThisRun.Where(lane => lane == TeamRoleKind.Conformity).ToList()
                    : EveryMergeLanesOfThisRun.ToList();

            if (!landing.IsPeriodic)
            {
                _merges++;
                if (PeriodicLanesOfThisRun.Count > 0 && _merges % PeriodicInterval == 0)
                {
                    lanes.AddRange(PeriodicLanesOfThisRun);
                }
            }

            if (lanes.Count == 0)
            {
                return null;
            }

            item = new ReviewItem(_nextId++, landing, lanes);
            _items.Add(item);
        }

        Signal();
        Enqueued?.Invoke(item);
        return item;
    }

    /// <summary>Queues the goal-level lanes' look at everything merged so far - the wrap-up item, and
    /// the same shape as their periodic one.</summary>
    public ReviewItem? EnqueuePeriodic(string toSha, string? note = null) =>
        Enqueue(new MergeLanding(toSha, toSha, "runner", [], PatchSummary: note, IsPeriodic: true));

    /// <summary>Raised after an item is queued, outside the lock.</summary>
    public event Action<ReviewItem>? Enqueued;

    /// <summary>
    /// The oldest item this lane has not started, marked taken. Null when the lane has nothing
    /// pending - which for a specialist means: wait, or wrap up.
    /// </summary>
    public ReviewItem? TakeNext(TeamRoleKind lane)
    {
        ReviewItem? taken = null;
        lock (_lock)
        {
            taken = _items.FirstOrDefault(item => item.TryTake(lane));
        }

        if (taken is not null)
        {
            Signal();
        }

        return taken;
    }

    /// <summary>Marks this lane's part of an item finished. Releasing it instead puts it back to
    /// pending, for a specialist whose cycle ended before it finished.</summary>
    public void Complete(ReviewItem item, TeamRoleKind lane)
    {
        item.MarkDone(lane);
        Signal();
    }

    public void Release(ReviewItem item, TeamRoleKind lane)
    {
        item.Release(lane);
        Signal();
    }

    /// <summary>
    /// team_next_review with an empty lane: holds the specialist, without a model call, until an item
    /// is queued for its lane, the queue closes, or the wait runs out - the same bargain as
    /// team_wait. Returns null when it timed out or the queue is closed and drained.
    /// </summary>
    public async Task<ReviewItem?> WaitForItemAsync(TeamRoleKind lane, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = _time.GetUtcNow() + wait;
        while (true)
        {
            var item = TakeNext(lane);
            if (item is not null)
            {
                return item;
            }

            if (Drained && IsClosed)
            {
                return null;
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var changed = CurrentSignal();
            if (changed.IsCompleted)
            {
                // Something happened between taking the signal and asking for it; look again.
                continue;
            }

            var delay = Task.Delay(remaining, _time, cancellationToken);
            var winner = await Task.WhenAny(changed, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (winner == delay)
            {
                return null;
            }
        }
    }

    /// <summary>Stops taking new items. Existing ones stay takeable, so a specialist that looks after
    /// the close still finishes what was already merged.</summary>
    public void Close()
    {
        lock (_lock)
        {
            IsClosed = true;
        }

        Signal();
    }

    /// <summary>Reopens the queue - a run that goes back to work after its deadline reminder should
    /// not lose the merges that follow.</summary>
    public void Open()
    {
        lock (_lock)
        {
            IsClosed = false;
        }
    }

    /// <summary>How many items this lane still has pending or taken - what the run shows next to a
    /// specialist, and what wrap-up counts.</summary>
    public int OutstandingFor(TeamRoleKind lane)
    {
        lock (_lock)
        {
            return _items.Count(item => item.StatusFor(lane) != ReviewItemStatus.Done);
        }
    }

    /// <summary>One line per lane for the report and the worker row: "mut 1 pending, 2 done".</summary>
    public string Describe()
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return IsClosed ? "no merges were queued" : "nothing queued yet";
            }

            var lanes = Lanes;
            return string.Join("; ", lanes.Select(lane =>
            {
                var pending = _items.Count(item => item.StatusFor(lane) == ReviewItemStatus.Pending);
                var taken = _items.Count(item => item.StatusFor(lane) == ReviewItemStatus.Taken);
                var done = _items.Count(item => item.StatusFor(lane) == ReviewItemStatus.Done);
                return $"{lane} {pending} pending, {taken} taken, {done} done";
            }));
        }
    }

    private Task CurrentSignal()
    {
        lock (_lock)
        {
            return _changed.Task;
        }
    }

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (_lock)
        {
            previous = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }
}
