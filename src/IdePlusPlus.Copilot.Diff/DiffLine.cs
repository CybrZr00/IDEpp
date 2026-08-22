namespace IdePlusPlus.Copilot.Diff;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
}

/// <summary>One word/whitespace-delimited run within a modified line's text. IsHighlighted marks the
/// specific run that actually changed (Deleted on the old side, Inserted on the new side) - the
/// rest of the line's text is carried too (IsHighlighted false) so the full line still renders.</summary>
public sealed record DiffTextSegment(string Text, bool IsHighlighted);

/// <summary>Segments is null for an unchanged line or a wholly added/removed line - only a line that
/// was modified in place carries a word-level breakdown for secondary highlighting.</summary>
public sealed record DiffLine(string Text, DiffLineKind Kind, IReadOnlyList<DiffTextSegment>? Segments = null)
{
    public bool HasSegments => Segments is { Count: > 0 };
}
