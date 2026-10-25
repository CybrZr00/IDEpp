namespace IdePlusPlus.SourceControl;

/// <summary>
/// ConflictedPaths is empty unless Status is Conflicted; then it lists the repo-root-relative,
/// forward-slash paths git left conflict markers in. Message is git's output - on a conflict that
/// includes its "CONFLICT (content): ..." lines, which say what kind of conflict each one is.
/// </summary>
public sealed record TeamSyncResult(TeamSyncStatus Status, IReadOnlyList<string> ConflictedPaths, string Message);

public enum TeamSyncStatus
{
    /// <summary>The branch had nothing the worktree didn't already have; HEAD didn't move.</summary>
    UpToDate,

    /// <summary>Fast-forwarded, or merged cleanly with a merge commit.</summary>
    Merged,

    /// <summary>The merge is left in progress with markers in the files, for the worker to resolve
    /// and then conclude with CommitAllAsync.</summary>
    Conflicted,

    /// <summary>Git refused before merging anything (for instance, uncommitted changes the merge
    /// would overwrite); the worktree is unchanged.</summary>
    Failed,
}
