namespace IdePlusPlus.SourceControl;

/// <summary>
/// Branch is null for a detached HEAD. Ahead/Behind are 0 when there's no upstream configured (git
/// doesn't report a count in that case, and there's nothing meaningful to show).
/// </summary>
public sealed record GitStatus(
    string? Branch,
    int Ahead,
    int Behind,
    IReadOnlyList<GitFileChange> Staged,
    IReadOnlyList<GitFileChange> Unstaged);
