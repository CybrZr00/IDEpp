namespace IdePlusPlus.Copilot.Diff;

public enum DiffLineKind
{
    Unchanged,
    Added,
    Removed,
}

public sealed record DiffLine(string Text, DiffLineKind Kind);
