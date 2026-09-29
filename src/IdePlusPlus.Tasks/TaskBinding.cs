namespace IdePlusPlus.Tasks;

/// <summary>
/// When a bound task runs. Deliberately its own enum rather than a reference to
/// IdePlusPlus.PluginContracts.BuildEventKind: that is a frozen public contract for plugin authors,
/// and this project has no business depending on it. The shell maps between the two.
/// </summary>
public enum TaskBindingKind
{
    BeforeBuild,
    AfterBuild,
    BeforeClean,
    AfterClean,
}

/// <summary>
/// A task set to run automatically at a lifecycle point.
///
/// <paramref name="ManifestRelativePath"/> is relative to the solution directory, with forward
/// slashes, for the same reason PublishSelectionStore keys on solution-relative project paths: the
/// file survives the solution folder being moved or cloned somewhere else.
/// </summary>
public sealed record TaskBinding(string ManifestRelativePath, string TaskName, TaskBindingKind Kind);
