namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>A clock that only moves when a test says so - for claim expiry and anything else
/// measured against TimeProvider.GetUtcNow.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public ManualTimeProvider() : this(new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero))
    {
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
