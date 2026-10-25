namespace IdePlusPlus.SourceControl;

/// <summary>
/// Commit is the worktree's HEAD after the call: the new commit when Committed, the unchanged HEAD
/// when NothingToCommit (so a caller tracking "the commit that last built" has a sha either way),
/// and null when Failed. Message is git's output, or why nothing was committed.
/// </summary>
public sealed record TeamCommitResult(TeamCommitStatus Status, string? Commit, string Message);

public enum TeamCommitStatus
{
    Committed,
    NothingToCommit,
    Failed,
}
