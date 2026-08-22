namespace IdePlusPlus.SourceControl;

/// <summary>Output is stdout+stderr combined (in that order) - git puts most human-facing progress
/// and error text on stderr even on success, so callers displaying a result to a user want both.</summary>
public sealed record GitCommandResult(bool Success, string Output);
