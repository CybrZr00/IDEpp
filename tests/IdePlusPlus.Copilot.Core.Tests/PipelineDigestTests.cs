using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>The history of the loop that the next round's prompts carry: what each owner reported, what the lead set aside, and nothing
/// about any judge's findings.</summary>
public sealed class PipelineDigestTests
{
    private static LoopHistoryEntry Round(int round, int open = 2, int score = 3) =>
        LoopHistoryEntry.Begin(round, "builds; tests pass", judgesRan: true, pass: 3, fail: 1, unavailable: 0, open, score);

    [Fact]
    public void NoHistory_IsNoDigest()
    {
        PipelineDigest.Render([]).ShouldBe("");
    }

    [Fact]
    public void ARoundWithNothingDoneAboutIt_IsOneLine()
    {
        PipelineDigest.Render([Round(1)]).ReplaceLineEndings("\n").ShouldBe(
            "Round 1: gate: builds; tests pass; judges: 3 pass, 1 fail; still open: 2 (score 3).");
    }

    [Fact]
    public void ARoundWithRepairsAndSetAsideFindings_SaysWhatEachOwnerDidAndCouldNotDo()
    {
        var entry = Round(1) with
        {
            Repairs =
            [
                new RepairNote("b1", ["F1.1", "F1.3"], "Guarded the null case.", []),
                new RepairNote("b2", ["F1.2"], null, ["F1.2: needs a redesign of the contract.", "F1.4: I disagree with the judge."]),
            ],
            Dismissed = ["Value should be a parameter (the design chose this on purpose)"],
            Deferred = ["Parsing is slow"],
        };

        PipelineDigest.Render([entry]).ReplaceLineEndings("\n").ShouldBe(
            "Round 1: gate: builds; tests pass; judges: 3 pass, 1 fail; still open: 2 (score 3).\n"
            + "  b1 repaired for F1.1, F1.3: Guarded the null case.\n"
            + "  b2 repaired for F1.2: (no report)\n"
            + "    could not: F1.2: needs a redesign of the contract.\n"
            + "    could not: F1.4: I disagree with the judge.\n"
            + "  dismissed by the lead: Value should be a parameter (the design chose this on purpose)\n"
            + "  left to the user: Parsing is slow");
    }

    [Fact]
    public void ARoundThatSkippedTheJudges_SaysSo_AndANonResponsiveJudgeIsCounted()
    {
        var skipped = LoopHistoryEntry.Begin(1, "does not build (1 error(s))", judgesRan: false, 0, 0, 0, open: 1, score: 3);
        var gave = LoopHistoryEntry.Begin(2, "builds", judgesRan: true, pass: 2, fail: 1, unavailable: 1, open: 1, score: 1);

        var digest = PipelineDigest.Render([skipped, gave]).ReplaceLineEndings("\n");

        digest.ShouldContain("Round 1: gate: does not build (1 error(s)); judges: not run; still open: 1 (score 3).");
        digest.ShouldContain("Round 2: gate: builds; judges: 2 pass, 1 fail, 1 gave no verdict; still open: 1 (score 1).");
    }

    [Fact]
    public void Rounds_AreInOrder_AndWhatIsLongIsCutToALine()
    {
        var long400 = new string('x', 400);
        var entry = Round(2) with { Repairs = [new RepairNote("b1", ["F1.1"], "First line.\r\nSecond line. " + long400, [])] };

        var digest = PipelineDigest.Render([Round(1), entry]).ReplaceLineEndings("\n");

        digest.IndexOf("Round 1:", StringComparison.Ordinal).ShouldBeLessThan(digest.IndexOf("Round 2:", StringComparison.Ordinal));
        digest.ShouldContain("b1 repaired for F1.1: First line. Second line. ");
        digest.ShouldContain("...");
        digest.ShouldNotContain(long400);
    }

    [Fact]
    public void WhenTheHistoryIsTooLong_TheEarliestRoundsAreLeftOut_NotTheLatest()
    {
        var rounds = Enumerable.Range(1, 20)
            .Select(i => Round(i) with { Repairs = [new RepairNote("b1", ["F1.1"], $"Repair number {i} " + new string('y', 120), [])] })
            .ToList();

        var digest = PipelineDigest.Render(rounds);

        digest.Length.ShouldBeLessThanOrEqualTo(PipelineDigest.MaxChars);
        digest.ShouldStartWith("(Earlier rounds are left out of this history.)");
        digest.ShouldContain("Round 20:");
        digest.ShouldNotContain("Round 1:");
    }

    [Fact]
    public void EvenOneHugeRound_IsCutToTheLimit()
    {
        var huge = Round(1) with { Repairs = [.. Enumerable.Range(0, 80).Select(i => new RepairNote($"b{i}", ["F1.1"], new string('z', 150), [new string('u', 150)]))] };

        var digest = PipelineDigest.Render([huge]);

        digest.Length.ShouldBeLessThanOrEqualTo(PipelineDigest.MaxChars);
        digest.ShouldStartWith("Round 1:");
        digest.ShouldEndWith("...");
    }
}
