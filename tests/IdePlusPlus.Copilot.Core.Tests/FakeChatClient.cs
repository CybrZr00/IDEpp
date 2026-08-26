using System.Runtime.CompilerServices;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// Replays a scripted sequence of turns - each call to StreamChatAsync consumes the next queued
/// turn's events, in order, yielding one at a time so tests exercise the same "many small
/// continuations" shape a real SSE stream has. Every request's message history is recorded so a
/// test can assert history was threaded through a loop correctly (e.g. that a tool result actually
/// made it into the next request).
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatStreamEvent>> _turns;

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public FakeChatClient(params IReadOnlyList<ChatStreamEvent>[] turns)
    {
        _turns = new Queue<IReadOnlyList<ChatStreamEvent>>(turns);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools = null,
        ChatEffort effort = ChatEffort.None,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages);

        if (_turns.Count == 0)
        {
            yield break;
        }

        foreach (var streamEvent in _turns.Dequeue())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return streamEvent;
        }
    }
}
