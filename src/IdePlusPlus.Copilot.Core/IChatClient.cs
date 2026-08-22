namespace IdePlusPlus.Copilot.Core;

public interface IChatClient
{
    IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools = null,
        CancellationToken cancellationToken = default);
}
