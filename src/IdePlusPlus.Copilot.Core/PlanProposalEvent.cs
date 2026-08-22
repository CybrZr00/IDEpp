namespace IdePlusPlus.Copilot.Core;

public enum PlanProposalStatus
{
    Streaming,
    Complete,
}

public sealed class PlanProposalEvent
{
    public required string ToolCallId { get; init; }

    public required PlanProposalStatus Status { get; init; }

    /// <summary>Best-effort live preview of the "summary" argument while still streaming.</summary>
    public string? SummaryPreview { get; init; }

    /// <summary>Only set once Status is Complete - the fully parsed, authoritative plan.</summary>
    public PlanOperation? Plan { get; init; }
}
