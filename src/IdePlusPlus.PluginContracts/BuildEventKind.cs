namespace IdePlusPlus.PluginContracts;

/// <summary>
/// A point in the build lifecycle a handler can be attached to. These are the same four points
/// Task Explorer's own bindings use - a plugin gets no more and no less than the IDE's own feature.
///
/// Deliberately not including a "project open" event: IDE++ loads a solution, not a project, and an
/// event whose name implies per-project granularity it does not have would be misleading.
/// </summary>
public enum BuildEventKind
{
    BeforeBuild,
    AfterBuild,
    BeforeClean,
    AfterClean,
}
