using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class StageBudgetTests
{
    [Fact]
    public void TheScheduleIsUnderstandDesignBuildThenAJudgeAndRepairPerLoopAndOneLastJudge()
    {
        var schedule = StageBudget.Schedule(2);

        schedule.ShouldBe(
        [
            new StageSlot(UltraStage.Understand, 0),
            new StageSlot(UltraStage.Design, 0),
            new StageSlot(UltraStage.Build, 0),
            new StageSlot(UltraStage.Judge, 1),
            new StageSlot(UltraStage.Repair, 1),
            new StageSlot(UltraStage.Judge, 2),
            new StageSlot(UltraStage.Repair, 2),
            new StageSlot(UltraStage.Judge, 3),
        ]);
    }

    [Fact]
    public void WithNoRepairLoopsThereIsOneJudgeRound_SoLandedCodeIsAlwaysJudged()
    {
        StageBudget.Schedule(0).Count(s => s.Stage == UltraStage.Judge).ShouldBe(1);
        StageBudget.Schedule(0).Any(s => s.Stage == UltraStage.Repair).ShouldBeFalse();
        StageBudget.Schedule(-5).Count.ShouldBe(4);
    }

    [Fact]
    public void OnlyTheAgentStagesHaveAWeight()
    {
        StageBudget.WeightOf(UltraStage.Understand).ShouldBe(2);
        StageBudget.WeightOf(UltraStage.Design).ShouldBe(2);
        StageBudget.WeightOf(UltraStage.Build).ShouldBe(5);
        StageBudget.WeightOf(UltraStage.Judge).ShouldBe(2);
        StageBudget.WeightOf(UltraStage.Repair).ShouldBe(3);

        foreach (var stage in new[] { UltraStage.Setup, UltraStage.Roster, UltraStage.Gate, UltraStage.Triage, UltraStage.Finalize })
        {
            StageBudget.WeightOf(stage).ShouldBe(0);
        }
    }

    [Fact]
    public void EachStageGetsItsWeightOutOfEverythingStillToCome()
    {
        var schedule = StageBudget.Schedule(3);
        // Weights: U2 D2 B5 | J2 R3 | J2 R3 | J2 R3 | J2 = 26 in all.

        StageBudget.Share(2600L, schedule, 0).ShouldBe(200); // 2/26
        StageBudget.Share(2400L, schedule, 1).ShouldBe(200); // 2/24 of what is left
        StageBudget.Share(2200L, schedule, 2).ShouldBe(500); // 5/22
        StageBudget.Share(1700L, schedule, 3).ShouldBe(200); // 2/17
    }

    [Fact]
    public void TheBuildCannotSpendTheLoopsShare()
    {
        var schedule = StageBudget.Schedule(3);

        // At the build, everything still to come is: build 5 + three loops of (judge 2 + repair 3) + the
        // last judge 2 = 22. The loop's 17 stay in the denominator, so the build can take 5/22 at most -
        // not all of what is left.
        var build = StageBudget.Share(1000L, schedule, 2);

        build.ShouldBe(1000L * 5 / 22);
        (1000L - build).ShouldBeGreaterThanOrEqualTo(1000L * 17 / 22);
    }

    [Fact]
    public void ATimeSavedEarlyFlowsForward()
    {
        var schedule = StageBudget.Schedule(1);
        var understand = StageBudget.Share(TimeSpan.FromMinutes(100), schedule, 0);

        // The understand stage finishes in a fifth of its share; the design is then given a bigger slice.
        var remainingAfterEarlyFinish = TimeSpan.FromMinutes(100) - understand / 5;
        var designWithSavings = StageBudget.Share(remainingAfterEarlyFinish, schedule, 1);
        var designWithoutSavings = StageBudget.Share(TimeSpan.FromMinutes(100) - understand, schedule, 1);

        designWithSavings.ShouldBeGreaterThan(designWithoutSavings);
    }

    [Fact]
    public void TheLastStageGetsEverythingThatIsLeft()
    {
        var schedule = StageBudget.Schedule(1);

        StageBudget.Share(777L, schedule, schedule.Count - 1).ShouldBe(777);
    }

    [Fact]
    public void SharesSumToNoMoreThanWasThereToSpend()
    {
        var schedule = StageBudget.Schedule(3);
        long remaining = 1_000_000;
        long spentTotal = 0;
        for (var i = 0; i < schedule.Count; i++)
        {
            var share = StageBudget.Share(remaining, schedule, i);
            share.ShouldBeGreaterThan(0);
            spentTotal += share;
            remaining -= share; // Every stage uses exactly its share.
        }

        spentTotal.ShouldBeLessThanOrEqualTo(1_000_000);
        remaining.ShouldBeLessThan(schedule.Count); // Only rounding is left over.
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-100L)]
    public void NothingLeftMeansNothingToGive(long remaining) =>
        StageBudget.Share(remaining, StageBudget.Schedule(2), 0).ShouldBe(0);

    [Fact]
    public void AnIndexOutsideTheScheduleGetsNothing()
    {
        StageBudget.Share(1000L, StageBudget.Schedule(1), -1).ShouldBe(0);
        StageBudget.Share(1000L, StageBudget.Schedule(1), 99).ShouldBe(0);
    }

    [Fact]
    public void AStageWithNoWeightGetsNoShare() =>
        StageBudget.Share(1000L, [new StageSlot(UltraStage.Gate, 1)], 0).ShouldBe(0);

    [Fact]
    public void TheTimeSpanOverloadAgreesWithTheTickMath()
    {
        var schedule = StageBudget.Schedule(2);
        var remaining = TimeSpan.FromHours(2);

        StageBudget.Share(remaining, schedule, 2).Ticks.ShouldBe(StageBudget.Share(remaining.Ticks, schedule, 2));
    }

    [Fact]
    public void HugeBudgetsDoNotOverflow()
    {
        StageBudget.Share(long.MaxValue / 2, StageBudget.Schedule(8), 2).ShouldBeGreaterThan(0);
    }
}
