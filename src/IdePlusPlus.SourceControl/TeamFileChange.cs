namespace IdePlusPlus.SourceControl;

/// <summary>RelativePath is repo-root-relative with forward slashes, as git reports it.</summary>
public sealed record TeamFileChange(string RelativePath, TeamFileChangeKind Kind);

/// <summary>A type change (file to symlink or back) reads as Modified. Renames never appear: they
/// are reported as a Deleted plus an Added.</summary>
public enum TeamFileChangeKind
{
    Added,
    Modified,
    Deleted,
}
