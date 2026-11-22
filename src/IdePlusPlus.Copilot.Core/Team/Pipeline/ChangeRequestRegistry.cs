namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>Where a request to change someone else's file stands.</summary>
public enum ChangeRequestState
{
    /// <summary>Waiting for the owner to answer.</summary>
    Pending,

    /// <summary>The owner will make the change; Done once it is on the team branch.</summary>
    Accepted,

    /// <summary>The owner said no, with reasons. The requester may appeal once.</summary>
    Rejected,

    /// <summary>Rejected, and the requester appealed: the orchestrator is deciding.</summary>
    Appealed,

    /// <summary>The change is on the team branch; the requester can sync and carry on.</summary>
    Done,

    TimedOut,

    /// <summary>Withdrawn because the requester or the owner has finished.</summary>
    Cancelled,
}

/// <summary>One request from an agent to the owner of a file: "change this for me". The change is
/// described, not made - the owner is the only agent that may edit the file.</summary>
public sealed record ChangeRequest(
    string Id,
    string From,
    string To,
    string File,
    string Change,
    string Rationale,
    ChangeRequestState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>The owner's note on accepting, or its reasoning for rejecting.</summary>
    public string? Reasoning { get; init; }

    public string? DoneSummary { get; init; }

    public string? AppealArgument { get; init; }

    /// <summary>The orchestrator's decision on an appeal.</summary>
    public string? Arbitration { get; init; }

    public bool AppealUsed { get; init; }

    /// <summary>True when the orchestrator ruled for the requester: the owner has to make the change.</summary>
    public bool Binding { get; init; }

    /// <summary>Nothing further happens to it unless the requester appeals a rejection.</summary>
    public bool IsSettled => State is ChangeRequestState.Done or ChangeRequestState.Rejected
        or ChangeRequestState.TimedOut or ChangeRequestState.Cancelled;

    /// <summary>Still needs something from someone.</summary>
    public bool IsOpen => State is ChangeRequestState.Pending or ChangeRequestState.Accepted or ChangeRequestState.Appealed;
}

/// <summary>An operation's outcome: the request as it now stands, or why it was refused.</summary>
public sealed record ChangeRequestResult(ChangeRequest? Request, string? Error)
{
    public bool Ok => Error is null;

    internal static ChangeRequestResult Success(ChangeRequest request) => new(request, null);

    internal static ChangeRequestResult Refused(string error) => new(null, error);
}

/// <summary>What <see cref="ChangeRequestRegistry.WaitForChangeAsync"/> found.</summary>
public sealed record ChangeWaitResult(ChangeRequest? Request, string? Error, bool WaitTimedOut)
{
    public bool Ok => Error is null;
}

/// <summary>
/// The pipeline's rule that only a file's owner edits it, made workable: an agent that needs another
/// agent's file asks (<see cref="Create"/>), carries on or waits (<see cref="WaitForChangeAsync"/>),
/// and the owner answers (<see cref="Respond"/>), makes the change and reports it
/// (<see cref="MarkDone"/>). A rejection comes with reasons and can be appealed once to the
/// orchestrator (<see cref="Appeal"/>, <see cref="Arbitrate"/>), so a disagreement ends rather than
/// going back and forth.
///
/// Two agents waiting on each other would wait forever, so a wait that would close a cycle is refused
/// when it starts - and the answer it is refused with is "answer them first". A request nobody
/// answers expires (<see cref="Sweep"/>), and one whose owner or requester has finished is cancelled.
///
/// Thread-safe: agents call it from their own threads. Waiting is by task, so a blocked agent costs
/// no model calls.
/// </summary>
public sealed class ChangeRequestRegistry
{
    public const int MaxChangeLength = 1500;
    public const int MaxRationaleLength = 600;
    public const int MaxReasoningLength = 600;

    /// <summary>A rejection must say why; "no" is not an answer another agent can act on.</summary>
    public const int MinRejectionReasoningLength = 20;

    /// <summary>The most requests one agent may have open at once.</summary>
    public const int MaxOpenPerRequester = 3;

    private readonly TimeProvider _time;
    private readonly TimeSpan _pendingTimeout;
    private readonly TimeSpan _acceptedTimeout;
    private readonly object _gate = new();
    private readonly List<ChangeRequest> _requests = [];
    private readonly List<ChangeRequest> _outbox = [];
    private readonly Dictionary<string, TaskCompletionSource> _signals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _waitingOn = new(StringComparer.OrdinalIgnoreCase);
    private int _nextNumber;

    /// <param name="pendingTimeout">How long a request may go unanswered before it times out.</param>
    /// <param name="acceptedTimeout">How long an accepted request may go unfulfilled.</param>
    public ChangeRequestRegistry(TimeProvider? time = null, TimeSpan? pendingTimeout = null, TimeSpan? acceptedTimeout = null)
    {
        _time = time ?? TimeProvider.System;
        _pendingTimeout = pendingTimeout ?? TimeSpan.FromMinutes(15);
        _acceptedTimeout = acceptedTimeout ?? TimeSpan.FromMinutes(30);
    }

    /// <summary>Raised after every change to a request - created, answered, done, expired - on the thread
    /// that caused it and outside the registry's lock. The runner uses it to wake an owner and to keep the
    /// status card and the record up to date.</summary>
    public event Action<ChangeRequest>? Changed;

    public IReadOnlyList<ChangeRequest> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public ChangeRequest? Find(string? id)
    {
        lock (_gate)
        {
            return FindLocked(id);
        }
    }

    /// <summary>The requests addressed to <paramref name="owner"/> that it has not answered yet - what it
    /// has to deal with before anything else.</summary>
    public IReadOnlyList<ChangeRequest> PendingFor(string owner)
    {
        try
        {
            lock (_gate)
            {
                SweepLocked(out var expired);
                Enqueue(expired);
                return [.. _requests.Where(r => r.State == ChangeRequestState.Pending && Same(r.To, owner))];
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Everything still open that involves <paramref name="agent"/> as requester or owner.</summary>
    public IReadOnlyList<ChangeRequest> OpenFor(string agent)
    {
        lock (_gate)
        {
            return [.. _requests.Where(r => r.IsOpen && (Same(r.To, agent) || Same(r.From, agent)))];
        }
    }

    /// <summary>What <paramref name="agent"/> is currently blocked on, if it is inside a wait - "CR3 from b2".</summary>
    public string? WaitingOnDescription(string agent)
    {
        lock (_gate)
        {
            return _waitingOn.TryGetValue(agent, out var owner) ? $"an answer from {owner}" : null;
        }
    }

    /// <summary>Opens a request from <paramref name="from"/> to <paramref name="to"/>, the owner of
    /// <paramref name="file"/>. Who owns the file is the caller's to know; this checks the rest.</summary>
    public ChangeRequestResult Create(string from, string to, string file, string change, string rationale)
    {
        change = DataFence.Strip(PipelineText.Clean(change));
        rationale = DataFence.Strip(PipelineText.Clean(rationale));
        if (Same(from, to))
        {
            return ChangeRequestResult.Refused($"{file} is your own file; change it yourself.");
        }

        if (change.Length == 0)
        {
            return ChangeRequestResult.Refused("Say what should change: the request needs a description of the change.");
        }

        if (change.Length > MaxChangeLength || rationale.Length > MaxRationaleLength)
        {
            return ChangeRequestResult.Refused(
                $"Keep the change under {MaxChangeLength} characters and the rationale under {MaxRationaleLength}; "
                + $"yours are {change.Length} and {rationale.Length}.");
        }

        if (rationale.Length == 0)
        {
            return ChangeRequestResult.Refused("Say why: the owner decides on the rationale, so the request needs one.");
        }

        try
        {
            lock (_gate)
            {
                SweepLocked(out var expired);
                Enqueue(expired);

                if (_requests.Count(r => r.IsOpen && Same(r.From, from)) >= MaxOpenPerRequester)
                {
                    return ChangeRequestResult.Refused(
                        $"You already have {MaxOpenPerRequester} requests open; wait for some to be answered first.");
                }

                var now = _time.GetUtcNow();
                var created = new ChangeRequest(
                    $"CR{++_nextNumber}", from, to, file, change, rationale, ChangeRequestState.Pending, now, now);
                _requests.Add(created);
                _signals[created.Id] = NewSignal();
                Enqueue([created]);
                return ChangeRequestResult.Success(created);
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>The owner's answer. Accepting means it will make the change; rejecting needs reasons.</summary>
    public ChangeRequestResult Respond(string id, string by, bool accept, string reasoning)
    {
        reasoning = DataFence.Strip(PipelineText.Clean(reasoning));
        if (!accept && reasoning.Length < MinRejectionReasoningLength)
        {
            return ChangeRequestResult.Refused(
                $"A rejection needs reasons the requester can act on (at least {MinRejectionReasoningLength} characters): "
                + "what is wrong with the request, or what to do instead.");
        }

        return Transition(
            id,
            r => !Same(r.To, by) ? $"{r.Id} is addressed to {r.To}; only the owner answers it." : null,
            r => r.State == ChangeRequestState.Pending ? null : $"{r.Id} is {Describe(r.State)}; it can only be answered once.",
            r => r with
            {
                State = accept ? ChangeRequestState.Accepted : ChangeRequestState.Rejected,
                Reasoning = Cut(reasoning, MaxReasoningLength),
            });
    }

    /// <summary>The owner reports an accepted change made and merged to the team branch.</summary>
    public ChangeRequestResult MarkDone(string id, string by, string summary)
    {
        summary = DataFence.Strip(PipelineText.Clean(summary));
        return Transition(
            id,
            r => !Same(r.To, by) ? $"{r.Id} is addressed to {r.To}; only the owner reports it done." : null,
            r => r.State == ChangeRequestState.Accepted ? null : $"{r.Id} is {Describe(r.State)}; only an accepted request can be done.",
            r => r with { State = ChangeRequestState.Done, DoneSummary = Cut(summary.Length == 0 ? "done" : summary, MaxReasoningLength) });
    }

    /// <summary>The requester contests a rejection, once, with an argument. The orchestrator decides.</summary>
    public ChangeRequestResult Appeal(string id, string by, string argument)
    {
        argument = DataFence.Strip(PipelineText.Clean(argument));
        if (argument.Length < MinRejectionReasoningLength)
        {
            return ChangeRequestResult.Refused(
                $"An appeal needs an argument (at least {MinRejectionReasoningLength} characters): why the owner's "
                + "reasons do not hold.");
        }

        return Transition(
            id,
            r => !Same(r.From, by) ? $"{r.Id} was made by {r.From}; only the requester appeals it." : null,
            r => r.State != ChangeRequestState.Rejected ? $"{r.Id} is {Describe(r.State)}; only a rejected request can be appealed."
                : r.AppealUsed ? $"{r.Id} was already appealed; the decision is final." : null,
            r => r with { State = ChangeRequestState.Appealed, AppealUsed = true, AppealArgument = Cut(argument, MaxReasoningLength) });
    }

    /// <summary>The orchestrator's decision on an appeal. For the requester, the request is accepted and
    /// <see cref="ChangeRequest.Binding"/> - the owner must make the change. Against, the rejection is final.</summary>
    public ChangeRequestResult Arbitrate(string id, bool requesterWins, string instruction)
    {
        instruction = DataFence.Strip(PipelineText.Clean(instruction));
        return Transition(
            id,
            _ => null,
            r => r.State == ChangeRequestState.Appealed ? null : $"{r.Id} is {Describe(r.State)}; only an appealed request is arbitrated.",
            r => r with
            {
                State = requesterWins ? ChangeRequestState.Accepted : ChangeRequestState.Rejected,
                Binding = requesterWins,
                Arbitration = Cut(instruction, MaxReasoningLength),
            });
    }

    /// <summary>Withdraws everything open that involves <paramref name="agent"/> - called when it
    /// finishes, since nobody is left to answer or to read the answer.</summary>
    public IReadOnlyList<ChangeRequest> CancelAllFor(string agent, string reason)
    {
        var cancelled = new List<ChangeRequest>();
        lock (_gate)
        {
            for (var i = 0; i < _requests.Count; i++)
            {
                var request = _requests[i];
                if (request.IsOpen && (Same(request.From, agent) || Same(request.To, agent)))
                {
                    _requests[i] = request with
                    {
                        State = ChangeRequestState.Cancelled,
                        UpdatedAt = _time.GetUtcNow(),
                        Reasoning = request.Reasoning ?? Cut(DataFence.Strip(reason), MaxReasoningLength),
                    };
                    cancelled.Add(_requests[i]);
                    Wake(request.Id);
                }
            }

            Enqueue(cancelled);
        }

        Flush();
        return cancelled;
    }

    /// <summary>Times out what has waited too long for an answer or for the work. Called whenever the
    /// registry is used, and by the runner on its own beat.</summary>
    public IReadOnlyList<ChangeRequest> Sweep()
    {
        List<ChangeRequest> expired;
        lock (_gate)
        {
            SweepLocked(out expired);
            Enqueue(expired);
        }

        Flush();
        return expired;
    }

    /// <summary>
    /// Waits for <paramref name="id"/> to change: to return when the owner answers, makes the change, or
    /// the request times out or is cancelled. A request that is already settled returns at once. Gives up
    /// after <paramref name="timeout"/> with <see cref="ChangeWaitResult.WaitTimedOut"/> - the request is
    /// not changed by that. Refused if waiting would close a cycle of agents each waiting on the next.
    /// </summary>
    public async Task<ChangeWaitResult> WaitForChangeAsync(
        string id, string waiter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (early, signal) = BeginWait(id, waiter);
        Flush();
        if (early is not null)
        {
            return early;
        }

        try
        {
            using var timer = new CancellationTokenSource();
            using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
            var delay = Task.Delay(timeout, _time, link.Token);
            var finished = await Task.WhenAny(signal!, delay);
            await timer.CancelAsync();
            cancellationToken.ThrowIfCancellationRequested();

            ChangeWaitResult result;
            lock (_gate)
            {
                SweepLocked(out var expired);
                Enqueue(expired);
                var current = FindLocked(id);
                result = new ChangeWaitResult(current, null, finished != signal && current is { IsSettled: false });
            }

            Flush();
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _waitingOn.Remove(waiter);
            }
        }
    }

    /// <summary>The synchronous half of a wait: either the answer without waiting (no such request, not
    /// yours, already settled, would deadlock), or the signal to wait on - with the wait registered, so a
    /// later waiter can see it when checking for a cycle.</summary>
    private (ChangeWaitResult? Early, Task? Signal) BeginWait(string id, string waiter)
    {
        lock (_gate)
        {
            SweepLocked(out var expired);
            Enqueue(expired);

            if (FindLocked(id) is not { } request)
            {
                return (new ChangeWaitResult(null, $"There is no request {id}.", false), null);
            }

            if (!Same(request.From, waiter) && !Same(request.To, waiter))
            {
                return (new ChangeWaitResult(
                    null, $"{request.Id} is between {request.From} and {request.To}; it is not yours to wait on.", false), null);
            }

            if (request.IsSettled)
            {
                return (new ChangeWaitResult(request, null, false), null);
            }

            // The agent this wait is on: the owner if the requester waits, the requester if the owner does.
            var onAgent = Same(request.From, waiter) ? request.To : request.From;
            if (WouldDeadlock(waiter, onAgent))
            {
                return (new ChangeWaitResult(
                    null,
                    $"Waiting on {onAgent} would deadlock: {onAgent} is already waiting, directly or through others, on you. "
                    + "Answer the requests addressed to you first (respond_change), then come back to this one.",
                    false), null);
            }

            _waitingOn[waiter] = onAgent;
            return (null, _signals[request.Id].Task);
        }
    }

    private ChangeRequestResult Transition(
        string id,
        Func<ChangeRequest, string?> whoMay,
        Func<ChangeRequest, string?> stateMay,
        Func<ChangeRequest, ChangeRequest> apply)
    {
        try
        {
            lock (_gate)
            {
                SweepLocked(out var expired);
                Enqueue(expired);

                var index = _requests.FindIndex(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                {
                    return ChangeRequestResult.Refused($"There is no request {id}.");
                }

                var current = _requests[index];
                if (whoMay(current) is { } notYours)
                {
                    return ChangeRequestResult.Refused(notYours);
                }

                if (stateMay(current) is { } wrongState)
                {
                    return ChangeRequestResult.Refused(wrongState);
                }

                var updated = apply(current) with { UpdatedAt = _time.GetUtcNow() };
                _requests[index] = updated;
                Wake(updated.Id);
                Enqueue([updated]);
                return ChangeRequestResult.Success(updated);
            }
        }
        finally
        {
            Flush();
        }
    }

    /// <summary>Whether <paramref name="waiter"/> waiting on <paramref name="onAgent"/> would leave a
    /// ring: onAgent, or whoever it waits on, and so on, ends up waiting on the waiter.</summary>
    private bool WouldDeadlock(string waiter, string onAgent)
    {
        var current = onAgent;
        for (var steps = 0; steps <= _waitingOn.Count; steps++)
        {
            if (Same(current, waiter))
            {
                return true;
            }

            if (!_waitingOn.TryGetValue(current, out var next))
            {
                return false;
            }

            current = next;
        }

        return true; // A longer chain than there are waiters means a ring somewhere.
    }

    private void SweepLocked(out List<ChangeRequest> expired)
    {
        expired = [];
        var now = _time.GetUtcNow();
        for (var i = 0; i < _requests.Count; i++)
        {
            var request = _requests[i];
            var limit = request.State switch
            {
                ChangeRequestState.Pending => _pendingTimeout,
                ChangeRequestState.Accepted => _acceptedTimeout,
                _ => (TimeSpan?)null,
            };
            if (limit is { } allowed && now - request.UpdatedAt >= allowed)
            {
                _requests[i] = request with
                {
                    State = ChangeRequestState.TimedOut,
                    UpdatedAt = now,
                    Reasoning = request.State == ChangeRequestState.Pending
                        ? $"{request.To} did not answer within {allowed.TotalMinutes:0} minutes."
                        : $"{request.To} accepted it but did not report it done within {allowed.TotalMinutes:0} minutes.",
                };
                expired.Add(_requests[i]);
                Wake(request.Id);
            }
        }
    }

    private ChangeRequest? FindLocked(string? id) =>
        _requests.FirstOrDefault(r => string.Equals(r.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the request's current signal - waking whoever waits - and puts a fresh one in
    /// its place for the next change. Called under the lock, after the state was updated.</summary>
    private void Wake(string id)
    {
        if (_signals.TryGetValue(id, out var signal))
        {
            _signals[id] = NewSignal();
            signal.TrySetResult();
        }
    }

    /// <summary>Queues events while the lock is held; <see cref="Flush"/> raises them once it is not, so
    /// a handler can call back into the registry (to wake an owner, say) without re-entering it.</summary>
    private void Enqueue(IEnumerable<ChangeRequest> requests)
    {
        lock (_gate)
        {
            _outbox.AddRange(requests);
        }
    }

    private void Flush()
    {
        List<ChangeRequest> events;
        lock (_gate)
        {
            if (_outbox.Count == 0)
            {
                return;
            }

            events = [.. _outbox];
            _outbox.Clear();
        }

        foreach (var request in events)
        {
            Changed?.Invoke(request);
        }
    }

    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string Cut(string text, int max) => PipelineText.Truncate(text, max, out _);

    private static string Describe(ChangeRequestState state) => state switch
    {
        ChangeRequestState.Pending => "waiting for the owner",
        ChangeRequestState.Accepted => "already accepted",
        ChangeRequestState.Rejected => "rejected",
        ChangeRequestState.Appealed => "being arbitrated",
        ChangeRequestState.Done => "done",
        ChangeRequestState.TimedOut => "timed out",
        _ => "cancelled",
    };
}
