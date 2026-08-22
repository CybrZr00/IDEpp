namespace IdePlusPlus.Copilot.Core;

public abstract record ChatStreamEvent;

public sealed record TextDelta(string Text) : ChatStreamEvent;

public sealed record StreamCompleted : ChatStreamEvent;

public sealed record StreamError(string Message) : ChatStreamEvent;
