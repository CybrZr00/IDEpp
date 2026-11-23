using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// A run's allowance and how it is cut into stage shares. The numbers are chosen so the arithmetic can
/// be checked by hand: with three repair loops the schedule's weights add up to 26 (understand 2, design
/// 2, build 5, three rounds of judge 2 + repair 3, a last judge 2), and a 26 minute budget makes one
/// weight one minute.
/// </summary>
public sealed class PipelineBudgetTests
{
    private readonly ManualTimeProvider _time = new();
    private long _tokens;

    private PipelineBudget Budget(int minutes = 26, long maxTokens = 0, int loops = 3) => new(
        _time, TimeSpan.FromMinutes(minutes), maxTokens, loops, TimeSpan.FromMinutes(2), () => _tokens);

    private static TimeSpan Fraction(TimeSpan whole, int numerator, int denominator) =>
        TimeSpan.FromTicks(whole.Ticks * numerator / denominator);

    [Fact]
    public void TheFirstStage_GetsItsWeightsShareOfTheWholeRun()
    {
        using var clock = Budget().BeginStage(UltraStage.Understand);

        clock.TimeShare.ShouldBe(TimeSpan.FromMinutes(2));
        clock.TimeLeft.ShouldBe(TimeSpan.FromMinutes(2));
        clock.Stage.ShouldBe(UltraStage.Understand);
    }

    [Fact]
    public void AStageThatFinishesEarly_HandsItsUnusedTimeToTheOnesAfterIt()
    {
        var budget = Budget();
        using (var understand = budget.BeginStage(UltraStage.Understand))
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            understand.IsOver.ShouldBeFalse();
        }

        // 25.5 minutes remain and the weights from design on add up to 24.
        using var design = budget.BeginStage(UltraStage.Design);

        design.TimeShare.ShouldBe(Fraction(TimeSpan.FromMinutes(25.5), 2, 24));
        design.TimeShare.ShouldBeGreaterThan(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void TheBuild_CannotSpendWhatTheJudgeAndRepairLoopIsDue()
    {
        var budget = Budget();

        using var build = budget.BeginStage(UltraStage.Build);

        // Five of the 22 weights still to come, so more than two thirds stays in reserve.
        build.TimeShare.ShouldBe(Fraction(TimeSpan.FromMinutes(26), 5, 22));
        (budget.TimeLeft - build.TimeShare).ShouldBeGreaterThan(Fraction(budget.TimeLeft, 2, 3));
    }

    [Fact]
    public void EachJudgeAndRepairRound_IsCutFromWhatIsLeftWhenItStarts()
    {
        var budget = Budget();
        budget.Start();
        _time.Advance(TimeSpan.FromMinutes(11));

        using var judge = budget.BeginStage(UltraStage.Judge, round: 2);
        using var repair = budget.BeginStage(UltraStage.Repair, round: 2);

        // 15 minutes left. From judge round 2 the weights are 2+3 (round 2) + 2+3 (round 3) + 2 = 12.
        judge.TimeShare.ShouldBe(Fraction(TimeSpan.FromMinutes(15), 2, 12));
        // From repair round 2: 3 + 2+3 + 2 = 10.
        repair.TimeShare.ShouldBe(Fraction(TimeSpan.FromMinutes(15), 3, 10));
        judge.Round.ShouldBe(2);
    }

    [Theory]
    [InlineData(UltraStage.Setup, 0)]
    [InlineData(UltraStage.Gate, 1)]
    [InlineData(UltraStage.Finalize, 0)]
    [InlineData(UltraStage.Judge, 9)] // a round the schedule does not have
    public void AStageWithNoSlot_GetsAllThatIsLeft_SoTheRunsBudgetIsStillItsLimit(UltraStage stage, int round)
    {
        var budget = Budget();
        budget.Start();
        _time.Advance(TimeSpan.FromMinutes(6));

        using var clock = budget.BeginStage(stage, round);

        clock.TimeShare.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Tokens_AreCutTheSameWay_AndCountedFromTheStagesOwnStart()
    {
        _tokens = 5_000;
        var budget = Budget(maxTokens: 31_000);

        // 26,000 tokens left, 2 weights in 26.
        using var clock = budget.BeginStage(UltraStage.Understand);

        clock.TokenShare.ShouldBe(2_000);
        clock.TokensUsed.ShouldBe(0);
        clock.TokensLeft.ShouldBe(2_000);

        _tokens += 1_500;
        clock.TokensUsed.ShouldBe(1_500);
        clock.IsOver.ShouldBeFalse();

        _tokens += 500;
        clock.IsOver.ShouldBeTrue();
        clock.OverBecause.ShouldBe(StageOverReason.Tokens);
        clock.TokensLeft.ShouldBe(0);
    }

    [Fact]
    public void WithNoTokenLimit_ThereIsNoTokenShare_AndTokensNeverEndAStage()
    {
        _tokens = 9_999_999;
        var budget = Budget(maxTokens: 0);

        using var clock = budget.BeginStage(UltraStage.Build);
        _tokens += 5_000_000;

        budget.TokensLeft.ShouldBeNull();
        clock.TokenShare.ShouldBeNull();
        clock.TokensLeft.ShouldBeNull();
        clock.IsOver.ShouldBeFalse();
        budget.IsSpent.ShouldBeFalse();
    }

    [Fact]
    public void AStage_IsOverWhenItsTimeShareIsUsed_NotBefore()
    {
        using var clock = Budget().BeginStage(UltraStage.Understand);

        _time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromTicks(1));
        clock.IsOver.ShouldBeFalse();
        clock.OverBecause.ShouldBeNull();

        _time.Advance(TimeSpan.FromTicks(1));
        clock.IsOver.ShouldBeTrue();
        clock.OverBecause.ShouldBe(StageOverReason.Time);

        // Long past it, the time left stays at zero rather than going negative.
        _time.Advance(TimeSpan.FromMinutes(30));
        clock.TimeLeft.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void ARunWithNoTimeLeft_IsSpent_AndSaysWhy()
    {
        var budget = Budget(minutes: 10);
        budget.Start();
        budget.IsSpent.ShouldBeFalse();
        budget.SpentBecause.ShouldBeNull();

        _time.Advance(TimeSpan.FromMinutes(10));

        budget.IsSpent.ShouldBeTrue();
        budget.TimeLeft.ShouldBe(TimeSpan.Zero);
        budget.SpentBecause.ShouldNotBeNull().ShouldContain("time budget");
    }

    [Fact]
    public void ARunWithNoTokensLeft_IsSpent_AndSaysWhy()
    {
        var budget = Budget(maxTokens: 10_000);
        budget.Start();

        _tokens = 10_000;

        budget.IsSpent.ShouldBeTrue();
        budget.TokensLeft.ShouldBe(0);
        budget.SpentBecause.ShouldNotBeNull().ShouldContain("token budget");
    }

    [Fact]
    public void TheClock_StartsOnce_WhenTheFirstStageBegins_AndSetupIsNotChargedToAStage()
    {
        var budget = Budget();
        _time.Advance(TimeSpan.FromMinutes(3)); // setup: not on the clock yet
        budget.Elapsed.ShouldBe(TimeSpan.Zero);

        using var first = budget.BeginStage(UltraStage.Understand);
        _time.Advance(TimeSpan.FromMinutes(1));
        budget.Start(); // a second start changes nothing

        budget.Elapsed.ShouldBe(TimeSpan.FromMinutes(1));
        first.TimeShare.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void ANegativeBudgetOrGrace_IsTreatedAsNone()
    {
        var budget = new PipelineBudget(_time, TimeSpan.FromMinutes(-5), -1, 3, TimeSpan.FromSeconds(-9), () => 0);

        budget.TimeLeft.ShouldBe(TimeSpan.Zero);
        budget.MaxTokens.ShouldBe(0);
        budget.HardStopGrace.ShouldBe(TimeSpan.Zero);
        budget.IsSpent.ShouldBeTrue();
    }

    [Fact]
    public void TheStageClock_NamesItsRound()
    {
        var budget = Budget();

        using var understand = budget.BeginStage(UltraStage.Understand);
        using var judge = budget.BeginStage(UltraStage.Judge, 2);

        understand.ToString().ShouldBe("Understand");
        judge.ToString().ShouldBe("Judge 2");
    }
}
