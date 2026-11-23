namespace IdePlusPlus.Shell.Team;

/// <summary>
/// How long one build of an Ultra run may take before it is stopped. A cold build of a large monorepo takes far longer than the
/// five minutes that suit a small solution, so Settings sets it (<c>UltraBuildTimeoutMinutes</c>) and the pane puts it here before
/// a run starts; the runs' builds - the check before the run, every merge's build, the gate - read it when they start one.
/// </summary>
public static class TeamBuildTimeout
{
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    public static TimeSpan Current { get; set; } = Default;
}
