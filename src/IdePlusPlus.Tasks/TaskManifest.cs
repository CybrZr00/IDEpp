namespace IdePlusPlus.Tasks;

/// <summary>One discovered task-bearing file and everything runnable in it.</summary>
public sealed record TaskManifest(
    TaskManifestKind Kind,
    string Path,
    NodePackageManager PackageManager,
    IReadOnlyList<TaskDefinition> Tasks);
