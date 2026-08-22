namespace IdePlusPlus.Copilot.Core;

public abstract record ChatStreamEvent;

public sealed record TextDelta(string Text) : ChatStreamEvent;

/// <summary>
/// One fragment of a streamed tool call. Id and FunctionName are only present on the first
/// delta for a given Index - later deltas for the same call carry only an ArgumentsFragment,
/// which must be concatenated in order to reconstruct the full JSON arguments string.
/// </summary>
public sealed record ToolCallDelta(int Index, string? Id, string? FunctionName, string? ArgumentsFragment) : ChatStreamEvent;

public sealed record StreamCompleted : ChatStreamEvent;

public sealed record StreamError(string Message) : ChatStreamEvent;
