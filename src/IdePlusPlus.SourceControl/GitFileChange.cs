namespace IdePlusPlus.SourceControl;

/// <summary>Path is repo-root-relative with forward slashes, as git itself reports it.</summary>
public sealed record GitFileChange(string Path, GitFileStatus Status, bool IsStaged);
