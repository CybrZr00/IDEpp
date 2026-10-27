namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// A propose_plan call whose arguments never parsed into a plan by the time its stream ended -
/// cut off, malformed, or missing "summary"/"steps".
/// </summary>
/// <param name="Reason">Why it didn't parse, worded for the person looking at the plan card.</param>
/// <param name="RawArguments">Everything the model sent as arguments, verbatim, for diagnosis.</param>
public sealed record PlanProposalFailure(string ToolCallId, string Reason, string RawArguments);
