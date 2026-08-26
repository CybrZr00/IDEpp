namespace IdePlusPlus.Copilot.Memory;

public enum ScheduleTriggerKind
{
    Interval,
    DailyAtTime,
}

/// <summary>
/// A user-authored recurring automation - see ScheduledAutomationService (IdePlusPlus.Shell) for how
/// it actually runs. Trigger fields are raw data here rather than an IScheduleTrigger instance, so
/// this project doesn't need to depend on Copilot.Core for what amounts to an enum and two numbers;
/// ScheduledAutomationService is where a definition's trigger fields become a real trigger to
/// evaluate.
///
/// Provider/Model are captured at creation time rather than following whatever the live chat's
/// current selection is, so changing the active chat model later doesn't silently change what an
/// existing schedule runs.
/// </summary>
public sealed record ScheduleDefinition(
    string Name,
    ScheduleTriggerKind TriggerKind,
    double IntervalHours,
    TimeSpan DailyTimeOfDay,
    string Prompt,
    string Provider,
    string Model,
    bool Enabled,
    DateTimeOffset? LastRunUtc,
    string? LastResultSummary);
