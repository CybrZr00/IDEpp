namespace IdePlusPlus.Copilot.Core;

/// <summary>The fully-parsed result of a completed propose_plan tool call.</summary>
public sealed record PlanOperation(string Summary, IReadOnlyList<string> Steps);
