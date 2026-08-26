using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class IntervalTriggerTests
{
    [Fact]
    public void ShouldFireAsOf_WhenNeverRun_IsTrueRegardlessOfInterval()
    {
        var trigger = new IntervalTrigger(IntervalHours: 24);

        trigger.ShouldFireAsOf(DateTimeOffset.Now, lastRunUtc: null).ShouldBeTrue();
    }

    [Fact]
    public void ShouldFireAsOf_BeforeTheIntervalHasElapsed_IsFalse()
    {
        var trigger = new IntervalTrigger(IntervalHours: 24);
        var now = DateTimeOffset.UtcNow;

        trigger.ShouldFireAsOf(now, lastRunUtc: now.AddHours(-23)).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFireAsOf_OnceTheIntervalHasElapsed_IsTrue()
    {
        var trigger = new IntervalTrigger(IntervalHours: 24);
        var now = DateTimeOffset.UtcNow;

        trigger.ShouldFireAsOf(now, lastRunUtc: now.AddHours(-24)).ShouldBeTrue();
        trigger.ShouldFireAsOf(now, lastRunUtc: now.AddHours(-25)).ShouldBeTrue();
    }

    [Fact]
    public void ShouldFireAsOf_SupportsSubHourIntervals()
    {
        var trigger = new IntervalTrigger(IntervalHours: 0.5);
        var now = DateTimeOffset.UtcNow;

        trigger.ShouldFireAsOf(now, lastRunUtc: now.AddMinutes(-29)).ShouldBeFalse();
        trigger.ShouldFireAsOf(now, lastRunUtc: now.AddMinutes(-30)).ShouldBeTrue();
    }
}

public class DailyAtTimeTriggerTests
{
    private static readonly TimeSpan NineAm = TimeSpan.FromHours(9);

    [Fact]
    public void ShouldFireAsOf_BeforeTheTimeOfDay_IsFalse()
    {
        var trigger = new DailyAtTimeTrigger(NineAm);
        var now = new DateTimeOffset(2026, 3, 5, 8, 59, 0, TimeSpan.Zero);

        trigger.ShouldFireAsOf(now, lastRunUtc: null).ShouldBeFalse();
    }

    [Fact]
    public void ShouldFireAsOf_AtOrAfterTheTimeOfDayAndNeverRun_IsTrue()
    {
        var trigger = new DailyAtTimeTrigger(NineAm);
        var now = new DateTimeOffset(2026, 3, 5, 9, 0, 0, TimeSpan.Zero);

        trigger.ShouldFireAsOf(now, lastRunUtc: null).ShouldBeTrue();
    }

    [Fact]
    public void ShouldFireAsOf_AlreadyRunEarlierTheSameDay_IsFalse()
    {
        var trigger = new DailyAtTimeTrigger(NineAm);
        var lastRun = new DateTimeOffset(2026, 3, 5, 9, 5, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 3, 5, 14, 0, 0, TimeSpan.Zero);

        trigger.ShouldFireAsOf(now, lastRunUtc: lastRun).ShouldBeFalse();
    }

    /// <summary>The whole point of "once a day" over "every 24 hours from the last run": a run that
    /// happened late one day (here, 9:05) still fires at the normal 9am the next day, rather than
    /// drifting to 9:05 or later.</summary>
    [Fact]
    public void ShouldFireAsOf_ThePreviousDayRanLateButTodayIsOnTime_StillFires()
    {
        var trigger = new DailyAtTimeTrigger(NineAm);
        var lastRun = new DateTimeOffset(2026, 3, 5, 9, 45, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 3, 6, 9, 0, 0, TimeSpan.Zero);

        trigger.ShouldFireAsOf(now, lastRunUtc: lastRun).ShouldBeTrue();
    }

    [Fact]
    public void ShouldFireAsOf_RanYesterdayAndItsAfterTimeOfDayToday_IsTrue()
    {
        var trigger = new DailyAtTimeTrigger(NineAm);
        var lastRun = new DateTimeOffset(2026, 3, 4, 9, 0, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 3, 5, 9, 0, 0, TimeSpan.Zero);

        trigger.ShouldFireAsOf(now, lastRunUtc: lastRun).ShouldBeTrue();
    }
}
