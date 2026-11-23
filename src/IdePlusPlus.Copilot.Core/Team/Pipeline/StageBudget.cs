namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>The stages of an Ultra Pipeline run, in the order they happen. (Named for the run, not
/// the worker: TeamWorkerPhase is already a worker's own state.)</summary>
public enum UltraStage
{
    /// <summary>Snapshot, team branch, worktrees, preflight build.</summary>
    Setup,

    /// <summary>The orchestrator decides how many surveyors and which extra judges.</summary>
    Roster,

    Understand,

    Design,

    Build,

    /// <summary>The deterministic checks - build, tests, style - that run before any judge.</summary>
    Gate,

    Judge,

    /// <summary>Open findings are grouped by file and handed to the files' owners.</summary>
    Triage,

    Repair,

    /// <summary>The report, the records, and the result.</summary>
    Finalize,
}

/// <summary>One position in the run's plan: a stage and which round of it (0 for the stages that
/// happen once; the judge and repair rounds count from 1).</summary>
public sealed record StageSlot(UltraStage Stage, int Round);

/// <summary>
/// How a run's time and tokens are shared out between its stages. Each stage is given its weight's
/// share of what is <em>left</em> when it starts, out of everything still to come:
/// <c>remaining * weight / (weight + the weights of every later stage)</c>. That gives the judge and
/// repair loop its reserve automatically - the build cannot spend the loop's share, because the loop's
/// weights are in the denominator while it runs - and a stage that finishes early hands its unused time
/// to the ones after it, because the next share is taken from a bigger remainder.
///
/// Stages that are the runner's own work (setup, the gate, triage, finalizing) have no weight: they
/// are bounded by their own timeouts, not by a share.
/// </summary>
public static class StageBudget
{
    public static int WeightOf(UltraStage stage) => stage switch
    {
        UltraStage.Understand => 2,
        UltraStage.Design => 2,
        UltraStage.Build => 5,
        UltraStage.Judge => 2,
        UltraStage.Repair => 3,
        _ => 0,
    };

    /// <summary>
    /// The agents' stages in the order they would run if every judge round failed until the cap:
    /// understand, design, build, then a judge round and a repair round for each of the
    /// <paramref name="maxRepairLoops"/>, and one last judge round, so what lands was always judged.
    /// </summary>
    public static IReadOnlyList<StageSlot> Schedule(int maxRepairLoops)
    {
        var loops = Math.Max(0, maxRepairLoops);
        var schedule = new List<StageSlot>
        {
            new(UltraStage.Understand, 0),
            new(UltraStage.Design, 0),
            new(UltraStage.Build, 0),
        };
        for (var loop = 1; loop <= loops; loop++)
        {
            schedule.Add(new StageSlot(UltraStage.Judge, loop));
            schedule.Add(new StageSlot(UltraStage.Repair, loop));
        }

        schedule.Add(new StageSlot(UltraStage.Judge, loops + 1));
        return schedule;
    }

    /// <summary>The share of <paramref name="remaining"/> that the stage at <paramref name="index"/> of
    /// <paramref name="schedule"/> gets, in the same unit (ticks, tokens...). Zero when nothing is
    /// left, the index is outside the schedule, or the stage has no weight.</summary>
    public static long Share(long remaining, IReadOnlyList<StageSlot> schedule, int index)
    {
        if (remaining <= 0 || index < 0 || index >= schedule.Count)
        {
            return 0;
        }

        var weight = WeightOf(schedule[index].Stage);
        if (weight == 0)
        {
            return 0;
        }

        long total = 0;
        for (var i = index; i < schedule.Count; i++)
        {
            total += WeightOf(schedule[i].Stage);
        }

        return (long)((decimal)remaining * weight / total);
    }

    public static TimeSpan Share(TimeSpan remaining, IReadOnlyList<StageSlot> schedule, int index) =>
        TimeSpan.FromTicks(Share(remaining.Ticks, schedule, index));
}
