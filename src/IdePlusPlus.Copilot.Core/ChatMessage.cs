namespace IdePlusPlus.Copilot.Core;

public enum ChatRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>One tool call an assistant message requested, for replaying it back into history once the result is known.</summary>
public sealed record ChatToolCall(string Id, string FunctionName, string ArgumentsJson);

/// <summary>
/// ToolCalls is set on an Assistant message that requested one or more tool calls (Content may be
/// empty). ToolCallId is set on a Tool message carrying that call's result. Both are optional so
/// plain System/User/Assistant messages are unaffected.
///
/// ReasoningContent is an Assistant message's reasoning trace, where the model streamed one. Most
/// providers never want it back and their clients ignore it; DeepSeek in thinking mode requires it
/// on every earlier assistant message of a request that offers tools, and answers 400 without it.
/// </summary>
public sealed record ChatMessage(
    ChatRole Role,
    string Content,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ToolCallId = null,
    string? ReasoningContent = null);
