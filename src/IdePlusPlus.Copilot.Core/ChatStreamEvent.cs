namespace IdePlusPlus.Copilot.Core;

public abstract record ChatStreamEvent;

public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>A fragment of the model's reasoning/thinking trace, distinct from its actual reply -
/// streamed separately by providers that support extended reasoning (requires a ChatEffort above
/// None; see OpenAiCompatibleChat.ToReasoningParameter). Purely for showing the user what the model
/// is doing while it works - never replayed back into request history.</summary>
public sealed record ReasoningDelta(string Text) : ChatStreamEvent;

/// <summary>
/// One fragment of a streamed tool call. Id and FunctionName are only present on the first
/// delta for a given Index - later deltas for the same call carry only an ArgumentsFragment,
/// which must be concatenated in order to reconstruct the full JSON arguments string.
/// </summary>
public sealed record ToolCallDelta(int Index, string? Id, string? FunctionName, string? ArgumentsFragment) : ChatStreamEvent;

public sealed record StreamCompleted : ChatStreamEvent;

/// <summary>Emitted once, typically on the final chunk, when the provider includes a usage block
/// (OpenAI-compatible APIs do this when the request sets stream_options.include_usage).</summary>
public sealed record UsageReceived(int PromptTokens, int CompletionTokens) : ChatStreamEvent;

public sealed record StreamError(string Message) : ChatStreamEvent;
