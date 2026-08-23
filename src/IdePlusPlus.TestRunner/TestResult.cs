namespace IdePlusPlus.TestRunner;

/// <summary>One test's outcome from a parsed .trx file. ErrorMessage/StackTrace are null for
/// passing/skipped tests, and best-effort (possibly null) for failures if the .trx's shape doesn't
/// match what TrxParser expects.</summary>
public sealed record TestResult(string FullyQualifiedName, TestOutcome Outcome, TimeSpan Duration, string? ErrorMessage, string? StackTrace);
