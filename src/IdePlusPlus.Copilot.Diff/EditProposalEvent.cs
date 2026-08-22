namespace IdePlusPlus.Copilot.Diff;

public enum EditProposalStatus
{
    Streaming,
    Complete,
}

public sealed class EditProposalEvent
{
    public required string ToolCallId { get; init; }

    public required EditProposalStatus Status { get; init; }

    /// <summary>Best-effort live preview of the "file" argument while still streaming.</summary>
    public string? FilePathPreview { get; init; }

    /// <summary>Best-effort live preview of the "new_text" argument while still streaming.</summary>
    public string? NewTextPreview { get; init; }

    /// <summary>Only set once Status is Complete - the fully parsed, authoritative operation.</summary>
    public EditOperation? Operation { get; init; }
}
