namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// A clock that only moves when a test says so, timers included: a CancellationTokenSource with a
/// delay, or a Task.Delay on this provider, fires when <see cref="Advance"/> takes the time past it -
/// which is what lets a test run a stage out of time, or let a hard stop fire, without waiting.
/// </summary>
public sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = start;

    public ManualClock() : this(new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time on, and runs every timer that has come due - once each, however far time
    /// moved - on the calling thread.</summary>
    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        DateTimeOffset now;
        lock (_gate)
        {
            _now += by;
            now = _now;
            due = [.. _timers.Where(t => t.IsDue(now))];
        }

        foreach (var timer in due)
        {
            timer.Fire(now);
        }
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset? _dueAt;
        private TimeSpan _period = Timeout.InfiniteTimeSpan;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    _dueAt = null;
                    clock._timers.Remove(this);
                    return true;
                }

                _dueAt = clock._now + dueTime;
                if (!clock._timers.Contains(this))
                {
                    clock._timers.Add(this);
                }
            }

            // A timer created already due (a zero delay) fires at once, as a real one does.
            if (dueTime <= TimeSpan.Zero)
            {
                Fire(clock.GetUtcNow());
            }

            return true;
        }

        public bool IsDue(DateTimeOffset now) => _dueAt is { } due && due <= now;

        public void Fire(DateTimeOffset now)
        {
            lock (clock._gate)
            {
                if (_dueAt is not { } due || due > now)
                {
                    return;
                }

                if (_period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero)
                {
                    _dueAt = null;
                    clock._timers.Remove(this);
                }
                else
                {
                    _dueAt = due + _period;
                }
            }

            callback(state);
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                _dueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
