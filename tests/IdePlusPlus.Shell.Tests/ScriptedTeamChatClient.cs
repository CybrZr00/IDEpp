using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// Plays each Ultra worker its own scripted replies, one per request. Workers run concurrently and
/// share one client, so the script is keyed by the worker's name, read from the system prompt
/// ("You are w1, ..."). Once a worker's script runs out it gets <see cref="WhenScriptEmpty"/>, by
/// default a plain-text reply with no tool call.
/// </summary>
public sealed partial class ScriptedTeamChatClient : IChatClient
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<ChatStreamEvent[]>> _scripts = new();
    private int _callIds;
    private int _inFlight;
    private int _maxInFlight;

    /// <summary>The most requests that were ever being answered at the same moment.</summary>
    public int MaxInFlight => Volatile.Read(ref _maxInFlight);

    public ConcurrentQueue<(string Worker, IReadOnlyList<ChatMessage> Messages)> Requests { get; } = new();

    public Func<string, ChatStreamEvent[]> WhenScriptEmpty { get; set; } = _ => [new TextDelta("Nothing more to do.")];

    public TimeSpan DelayPerReply { get; set; } = TimeSpan.Zero;

    public ScriptedTeamChatClient Script(string worker, params ChatStreamEvent[][] replies)
    {
        var queue = _scripts.GetOrAdd(worker, _ => new ConcurrentQueue<ChatStreamEvent[]>());
        foreach (var reply in replies)
        {
            queue.Enqueue(reply);
        }

        return this;
    }

    /// <summary>One reply that calls one tool.</summary>
    public ChatStreamEvent[] Call(string tool, object? arguments = null) =>
    [
        new ToolCallDelta(0, $"call-{Interlocked.Increment(ref _callIds)}", tool, JsonSerializer.Serialize(arguments ?? new { })),
    ];

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools = null,
        ChatEffort effort = ChatEffort.None,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var worker = WorkerName().Match(messages[0].Content) is { Success: true } match ? match.Groups[1].Value : "?";
        Requests.Enqueue((worker, [.. messages]));
        var inFlight = Interlocked.Increment(ref _inFlight);
        int max;
        while (inFlight > (max = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, inFlight, max) != max)
        {
        }

        try
        {
            if (DelayPerReply > TimeSpan.Zero)
            {
                await Task.Delay(DelayPerReply, cancellationToken);
            }

            var reply = _scripts.TryGetValue(worker, out var queue) && queue.TryDequeue(out var next)
                ? next
                : WhenScriptEmpty(worker);

            foreach (var streamEvent in reply)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
                yield return streamEvent;
            }

            yield return new UsageReceived(UsagePerReply.Prompt, UsagePerReply.Completion);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>The usage reported after every reply.</summary>
    public (int Prompt, int Completion) UsagePerReply { get; set; } = (100, 10);

    [GeneratedRegex(@"^You are (w\d+),")]
    private static partial Regex WorkerName();
}
