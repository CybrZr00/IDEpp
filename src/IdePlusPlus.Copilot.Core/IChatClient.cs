namespace IdePlusPlus.Copilot.Core;

public interface IChatClient
{
    IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        CancellationToken cancellationToken = default);
}
