namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>A channel post when To is null, otherwise a direct message to one worker.</summary>
public sealed record TeamMessage(string From, string? To, string Text, DateTimeOffset Time);

/// <summary>
/// An Ultra team's messaging: Agensh's split between a shared channel and direct messages, minus
/// the chat server it used.
///
/// The two differ in when they arrive, which is the point of having both. A channel post waits for
/// each worker's next cycle - announcements ("I'm introducing IFoo in Core") matter, but not enough
/// to interrupt someone mid-step. A direct message is delivered after the recipient's very next tool
/// call, because what it carries ("we're both about to edit Bar.cs") is worth less every step it
/// waits. Workers are told to use direct messages only for that, since each one interrupts.
///
/// Thread-safe; per-worker queues are drained by the worker's own dispatcher.
/// </summary>
public sealed class TeamMessageBus
{
    /// <summary>The author of everything the run itself says - kickoff, nudges, deadline reminders.</summary>
    public const string RunnerName = "runner";

    public const int MaxMessageLength = 2_000;

    private readonly object _lock = new();
    private readonly List<TeamMessage> _channel = [];
    private readonly Dictionary<string, int> _channelCursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<TeamMessage>> _direct = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public TeamMessageBus(TimeProvider time, IEnumerable<string> members)
    {
        _time = time;
        foreach (var member in members)
        {
            _direct[member] = new Queue<TeamMessage>();
        }
    }

    public IReadOnlyCollection<string> Members
    {
        get
        {
            lock (_lock)
            {
                return [.. _direct.Keys];
            }
        }
    }

    /// <summary>Adds a worker who joins mid-run. They get the channel from now on, not its history -
    /// the board, which their first cycle opens with, is what carries what the team has learned.</summary>
    public void AddMember(string member)
    {
        lock (_lock)
        {
            _direct.TryAdd(member, new Queue<TeamMessage>());
            _channelCursors[member] = _channel.Count;
        }
    }

    /// <summary>Raised after every post or direct message, outside the lock.</summary>
    public event Action<TeamMessage>? MessageSent;

    /// <summary>Posts to the channel; returns an error for the caller to relay, or null.</summary>
    public string? Post(string from, string? text)
    {
        if (Validate(text) is { } error)
        {
            return error;
        }

        var message = new TeamMessage(from, null, text!.Trim(), _time.GetUtcNow());
        lock (_lock)
        {
            _channel.Add(message);
        }

        MessageSent?.Invoke(message);
        return null;
    }

    public string? SendDirect(string from, string? to, string? text)
    {
        if (Validate(text) is { } error)
        {
            return error;
        }

        to = to?.Trim();
        TeamMessage message;
        lock (_lock)
        {
            if (to is null || !_direct.TryGetValue(to, out var queue))
            {
                return $"There is no worker '{to}'. The team is: {string.Join(", ", _direct.Keys)}.";
            }

            if (to == from)
            {
                return "That is you - a direct message goes to a peer.";
            }

            message = new TeamMessage(from, to, text!.Trim(), _time.GetUtcNow());
            queue.Enqueue(message);
        }

        MessageSent?.Invoke(message);
        return null;
    }

    /// <summary>A direct message to every member but the sender - how the runner says something
    /// that cannot wait for the next cycle, such as a deadline.</summary>
    public void Broadcast(string from, string text)
    {
        foreach (var member in Members.Where(m => m != from))
        {
            SendDirect(from, member, text);
        }
    }

    /// <summary>Whether a direct message is waiting for this worker, without taking it - for a wait that
    /// should end at once rather than sleep on something that has already arrived.</summary>
    public bool HasDirect(string worker)
    {
        lock (_lock)
        {
            return _direct.TryGetValue(worker, out var queue) && queue.Count > 0;
        }
    }

    public IReadOnlyList<TeamMessage> TakeDirect(string worker)
    {
        lock (_lock)
        {
            if (!_direct.TryGetValue(worker, out var queue))
            {
                return [];
            }

            var messages = queue.ToList();
            queue.Clear();
            return messages;
        }
    }

    /// <summary>Channel posts by others since this worker last took them.</summary>
    public IReadOnlyList<TeamMessage> TakeChannel(string worker)
    {
        lock (_lock)
        {
            var cursor = _channelCursors.GetValueOrDefault(worker);
            _channelCursors[worker] = _channel.Count;
            return _channel.Skip(cursor).Where(m => m.From != worker).ToList();
        }
    }

    public IReadOnlyList<TeamMessage> ChannelSnapshot()
    {
        lock (_lock)
        {
            return [.. _channel];
        }
    }

    private static string? Validate(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "A message needs some text."
        : text.Length > MaxMessageLength
            ? $"That message is {text.Length} characters; keep it under {MaxMessageLength} - put findings that "
              + "need more room on the board, with the long part in 'detail'."
            : null;
}
