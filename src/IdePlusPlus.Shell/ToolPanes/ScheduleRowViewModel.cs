using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>A display-only projection over one ScheduleDefinition - wraps rather than flattens it,
/// so an action on a row (toggling Enabled, deleting) can round-trip the full definition back
/// through ScheduledAutomationService.SaveSchedule via a `with` expression.</summary>
public sealed record ScheduleRowViewModel(ScheduleDefinition Source)
{
    public string Name => Source.Name;

    public bool Enabled => Source.Enabled;

    public string Prompt => Source.Prompt;

    public string TriggerDescription => Source.TriggerKind == ScheduleTriggerKind.DailyAtTime
        ? $"Daily at {Source.DailyTimeOfDay:hh\\:mm}"
        : $"Every {Source.IntervalHours:0.##}h";

    public string LastRunDescription => Source.LastRunUtc is { } lastRun
        ? $"Last run {lastRun.ToLocalTime():g}"
        : "Never run";

    public string? LastResultSummary => Source.LastResultSummary;
}
