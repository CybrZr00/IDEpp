namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Decides whether a schedule is due to fire. A pure function of "now" and "when it last ran" - no
/// side effects, no wall-clock state of its own - so the same trigger type can be evaluated by an
/// in-process timer today and, if a headless/Task-Scheduler mode is added later, from a completely
/// different entry point without any change here.
/// </summary>
public interface IScheduleTrigger
{
    bool ShouldFireAsOf(DateTimeOffset now, DateTimeOffset? lastRunUtc);
}

/// <summary>Fires every IntervalHours since the last run, or immediately if it has never run.</summary>
public sealed record IntervalTrigger(double IntervalHours) : IScheduleTrigger
{
    public bool ShouldFireAsOf(DateTimeOffset now, DateTimeOffset? lastRunUtc) =>
        lastRunUtc is null || now - lastRunUtc.Value >= TimeSpan.FromHours(IntervalHours);
}

/// <summary>
/// Fires once per calendar day, at or after TimeOfDay - "once a day", not "every 24 hours from the
/// last run", so a run that happens a little late one day doesn't drift the time it fires on later
/// days. TimeOfDay and "now" are compared exactly as given - it is up to the caller to pass values
/// in a consistent time zone (in practice, local time: a schedule set for "9am" means the user's own
/// 9am, so the caller should pass DateTimeOffset.Now, not .UtcNow).
/// </summary>
public sealed record DailyAtTimeTrigger(TimeSpan TimeOfDay) : IScheduleTrigger
{
    public bool ShouldFireAsOf(DateTimeOffset now, DateTimeOffset? lastRunUtc)
    {
        if (now.TimeOfDay < TimeOfDay)
        {
            return false;
        }

        return lastRunUtc is null || lastRunUtc.Value.Date < now.Date;
    }
}
