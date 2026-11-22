using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class TeamWorkerStateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    private readonly TestTime _time = new();

    private TeamWorkerState NewWorker(TeamWorkerPhase phase = TeamWorkerPhase.Working) =>
        new("b1", _time) { Phase = phase };

    [Fact]
    public void AWorkerThatIsNotWorkingIsNeverActive()
    {
        NewWorker(TeamWorkerPhase.Waiting).IsActive(Window).ShouldBeFalse();
        NewWorker(TeamWorkerPhase.Done).IsActive(Window).ShouldBeFalse();
        NewWorker(TeamWorkerPhase.Stopped).IsActive(Window).ShouldBeFalse();
    }

    [Fact]
    public void AWorkingWorkerIsActiveUntilItHasBeenQuietForLongerThanTheWindow()
    {
        var worker = NewWorker();
        worker.IsActive(Window).ShouldBeTrue(); // Not heard from yet: it has only just started.

        worker.RecordStep("read_file");
        _time.Advance(TimeSpan.FromMinutes(9));
        worker.IsActive(Window).ShouldBeTrue();

        _time.Advance(TimeSpan.FromMinutes(2));
        worker.IsActive(Window).ShouldBeFalse();

        worker.RecordStep("read_file");
        worker.IsActive(Window).ShouldBeTrue();
    }

    [Fact]
    public void AWorkerBlockedOnAnotherAgentStaysActiveHoweverLongItWaits()
    {
        var worker = NewWorker();
        worker.RecordStep("await_change");

        using (worker.BlockOn("CR3 from b2"))
        {
            worker.BlockedOn.ShouldBe("CR3 from b2");
            _time.Advance(TimeSpan.FromHours(1));
            worker.IsActive(Window).ShouldBeTrue();
        }

        worker.BlockedOn.ShouldBeNull();
        worker.IsActive(Window).ShouldBeFalse(); // Back to the ordinary rule: an hour is far too quiet.
    }

    [Fact]
    public void TheBlockIsLiftedEvenWhenTheWaitThrows()
    {
        var worker = NewWorker();

        Should.Throw<InvalidOperationException>(() =>
        {
            using (worker.BlockOn("CR1 from b2"))
            {
                throw new InvalidOperationException("the wait failed");
            }
        });

        worker.BlockedOn.ShouldBeNull();
    }

    [Theory]
    [InlineData(TeamWorkerPhase.Done)]
    [InlineData(TeamWorkerPhase.Stopped)]
    [InlineData(TeamWorkerPhase.Waiting)]
    public void BlockingDoesNotMakeAFinishedOrNotYetStartedWorkerActive(TeamWorkerPhase phase)
    {
        var worker = NewWorker(phase);

        using (worker.BlockOn("CR1 from b2"))
        {
            worker.IsActive(Window).ShouldBeFalse();
        }
    }

    [Fact]
    public void AWorkerThatWasNeverBlockedHasNothingToShow() => NewWorker().BlockedOn.ShouldBeNull();

    private sealed class TestTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
