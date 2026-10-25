namespace IdePlusPlus.SourceControl;

/// <summary>Message says what happened in words a worker can act on - for RefMoved and
/// NotFastForward, that it needs to sync the branch into its own and try again.</summary>
public sealed record TeamFastForwardResult(TeamFastForwardStatus Status, string Message);

public enum TeamFastForwardStatus
{
    FastForwarded,

    /// <summary>The new commit doesn't contain the branch's expected old commit, so moving the
    /// branch would drop someone's merged work. Nothing was changed.</summary>
    NotFastForward,

    /// <summary>The branch was no longer at the expected old commit by the time of the update -
    /// another worker merged first. Nothing was changed.</summary>
    RefMoved,

    Failed,
}
