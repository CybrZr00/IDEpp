using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>What the user is told when a pipeline run ends: how it went, what is still open with its evidence, and what they could ask
/// for next. The markdown is the golden file of report.md.</summary>
public sealed class PipelineReportTests
{
    private static ValidatedFinding Reported(
        string title,
        TeamFindingSeverity severity,
        string[] files,
        string? evidence = "Read it.",
        string? quote = "some code",
        string? fix = "Fix it properly.",
        FindingStatus status = FindingStatus.Open,
        string category = "correctness") =>
        new(new JudgeFinding(title, severity, category, files, evidence, quote, fix), status);

    private static PipelineReport Build(
        FindingLedger ledger,
        bool passed = false,
        string endReason = "LoopCapReached",
        IReadOnlyList<TriageDecision>? deferred = null,
        TeamFindingSeverity threshold = TeamFindingSeverity.Medium) =>
        PipelineReportBuilder.Build(ledger, deferred ?? [], threshold, passed, endReason, 3, 2, ["A gap."]);

    // ---- What counts as open ----

    [Fact]
    public void WhatKeepsARunFromPassing_IsOpen_MostSevereFirst_AndTheRestIsCountedOrListedBelowTheBar()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1",
        [
            Reported("A medium problem", TeamFindingSeverity.Medium, ["Lib/A.cs"]),
            Reported("A high problem", TeamFindingSeverity.High, ["Lib/B.cs"]),
            Reported("A low problem", TeamFindingSeverity.Low, ["Lib/C.cs"], evidence: null, quote: null),
            Reported("A fixed one", TeamFindingSeverity.Medium, ["Lib/D.cs"]),
            Reported("A dismissed one", TeamFindingSeverity.Medium, ["Lib/E.cs"]),
            Reported("An unverified one", TeamFindingSeverity.High, ["Lib/F.cs"], status: FindingStatus.Unverified),
        ]);
        ledger.SetStatus("F1.4", FindingStatus.Fixed);
        ledger.SetStatus("F1.5", FindingStatus.Dismissed);

        var report = Build(ledger);

        report.Open.Select(f => (f.Id, f.Severity, f.Title)).ShouldBe(
            [("F1.2", TeamFindingSeverity.High, "A high problem"), ("F1.1", TeamFindingSeverity.Medium, "A medium problem")]);
        report.Lower.Select(f => f.Title).ShouldBe(["A low problem"]);
        (report.Fixed, report.Dismissed, report.Unverified).ShouldBe((1, 1, 1));
        (report.JudgeRounds, report.RepairRounds, report.Threshold).ShouldBe((3, 2, TeamFindingSeverity.Medium));
        report.Gaps.ShouldBe(["A gap."]);
    }

    [Fact]
    public void ARaisedBar_MovesAMediumFindingBelowIt()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1",
        [
            Reported("A medium problem", TeamFindingSeverity.Medium, ["Lib/A.cs"]),
            Reported("A high problem", TeamFindingSeverity.High, ["Lib/B.cs"]),
        ]);

        var report = Build(ledger, threshold: TeamFindingSeverity.High);

        report.Open.Select(f => f.Title).ShouldBe(["A high problem"]);
        report.Lower.Select(f => f.Title).ShouldBe(["A medium problem"]);
    }

    [Fact]
    public void AFindingTheJudgeStoppedReporting_IsFixedNotOpen()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Reported("A problem", TeamFindingSeverity.High, ["Lib/A.cs"])]);
        ledger.CompleteRound(1, ["j1"]);
        ledger.RecordRound(2, "j1", []);
        ledger.CompleteRound(2, ["j1"]);

        var report = Build(ledger, passed: true, endReason: "AllDone");

        report.Open.ShouldBeEmpty();
        report.Fixed.ShouldBe(1);
    }

    // ---- What the user could ask for next ----

    [Fact]
    public void TheNextPrompts_AreWhatWasLeftToTheUser_ThenOneForEachOpenFinding_WithoutRepeats()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1",
        [
            Reported("A medium problem", TeamFindingSeverity.Medium, ["Lib/A.cs"], fix: "Guard it."),
            Reported("A high problem", TeamFindingSeverity.High, ["Lib/B.cs", "Lib/C.cs"], fix: null),
        ]);
        TriageDecision[] deferred =
        [
            new("F1.9", TriageAction.Defer, "Out of scope.", NextPrompt: "Cache the parse result."),
            new("F1.8", TriageAction.Defer, "Out of scope.", NextPrompt: "Cache the parse result."),
        ];

        var report = Build(ledger, deferred: deferred);

        report.NextPrompts.ShouldBe(
            [
                "Cache the parse result.",
                "In Lib/B.cs, Lib/C.cs: A high problem",
                "In Lib/A.cs: A medium problem. Guard it.",
            ]);
    }

    [Fact]
    public void TheNextPrompts_AreCapped()
    {
        var ledger = new FindingLedger();
        for (var i = 0; i < 20; i++)
        {
            ledger.RecordRound(1, "j1", [Reported($"Problem number {i} unlike the others {Guid.NewGuid():N}", TeamFindingSeverity.Medium, [$"Lib/F{i}.cs"])]);
        }

        Build(ledger).NextPrompts.Count.ShouldBe(PipelineReport.MaxNextPrompts);
    }

    // ---- How it is put ----

    [Theory]
    [InlineData(true, "AllDone", 1, 1, "Passed: every judge was satisfied after 1 judge round and 1 repair round.")]
    [InlineData(true, "AllDone", 3, 2, "Passed: every judge was satisfied after 3 judge rounds and 2 repair rounds.")]
    [InlineData(false, "LoopCapReached", 4, 3, "Stopped at its limit of 3 repair rounds with 2 findings still open.")]
    [InlineData(false, "NoProgress", 3, 2, "Stopped early: more repair rounds were not making progress. 2 findings still open.")]
    [InlineData(false, "BudgetSpent", 1, 0, "Ended early (BudgetSpent). 2 findings still open.")]
    public void TheHeadline_SaysHowTheRunEnded(bool passed, string endReason, int judgeRounds, int repairRounds, string expected)
    {
        var finding = Reported("A problem", TeamFindingSeverity.Medium, ["Lib/A.cs"]);
        var ledger = new FindingLedger();
        if (!passed)
        {
            ledger.RecordRound(1, "j1", [finding, Reported("Another problem entirely", TeamFindingSeverity.High, ["Lib/B.cs"])]);
        }

        var report = PipelineReportBuilder.Build(ledger, [], TeamFindingSeverity.Medium, passed, endReason, judgeRounds, repairRounds, []);

        report.Headline.ShouldBe(expected);
    }

    [Fact]
    public void OneOpenFinding_IsSingular()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Reported("A problem", TeamFindingSeverity.Medium, ["Lib/A.cs"])]);

        Build(ledger).Headline.ShouldBe("Stopped at its limit of 2 repair rounds with 1 finding still open.");
    }

    private static PipelineReport GoldenReport() => new(
        Passed: false,
        EndReason: "LoopCapReached",
        JudgeRounds: 3,
        RepairRounds: 2,
        Threshold: TeamFindingSeverity.Medium,
        Open:
        [
            new ReportFinding(
                "F1.1", "j4", TeamFindingSeverity.High, "correctness", "A race in Parse", ["Lib/A.cs"],
                "A failing test on branch:abc1234.", "lock (_gate) { return _value; }", "Take the lock.", FindingStatus.Escalated, 2,
                "reported again after a repair (seen in 2 loops)"),
        ],
        Lower: [new ReportFinding("F2.1", "j2", TeamFindingSeverity.Low, "maintainability", "Name unclear", ["Lib/B.cs"], null, null, null, FindingStatus.Open, 1, null)],
        Fixed: 3,
        Dismissed: 1,
        Unverified: 0,
        NextPrompts: ["In Lib/A.cs: A race in Parse. Take the lock."],
        Gaps: ["The gate failed in round 1: does not build (1 error(s))."]);

    [Fact]
    public void TheReportFile_IsAsItIsDocumented()
    {
        const string Expected = """
            # Ultra pipeline report

            Stopped at its limit of 2 repair rounds with 1 finding still open.

            - Judge rounds: 3. Repair rounds: 2. Severity bar: medium and above.
            - Fixed: 3. Dismissed or left for you: 1. Set aside as unverified: 0.

            ## Open findings

            ### [high] A race in Parse

            F1.1 - found by j4, escalated, seen in 2 rounds. Files: Lib/A.cs.

            Evidence: A failing test on branch:abc1234.

            ```
            lock (_gate) { return _value; }
            ```

            Suggested fix: Take the lock.

            Note: reported again after a repair (seen in 2 loops)

            ## Below the bar (not repaired)

            - [low] Name unclear (Lib/B.cs)

            ## Suggested next prompts

            1. In Lib/A.cs: A race in Parse. Take the lock.

            ## Gaps in the record

            - The gate failed in round 1: does not build (1 error(s)).

            """;

        GoldenReport().ToMarkdown().ReplaceLineEndings("\n").ShouldBe(Expected.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ThePassedReport_HasNoOpenSections()
    {
        var report = new PipelineReport(true, "AllDone", 1, 0, TeamFindingSeverity.Medium, [], [], 0, 0, 0, [], []);

        report.ToMarkdown().ReplaceLineEndings("\n").ShouldBe(
            "# Ultra pipeline report\n\nPassed: every judge was satisfied after 1 judge round and 0 repair rounds.\n\n"
            + "- Judge rounds: 1. Repair rounds: 0. Severity bar: medium and above.\n"
            + "- Fixed: 0. Dismissed or left for you: 0. Set aside as unverified: 0.\n");
        report.ToSummary().ShouldBe("Passed: every judge was satisfied after 1 judge round and 0 repair rounds.");
    }

    [Fact]
    public void TheSummary_NamesTheMostSevereFindingsWithTheirEvidence_AndTheNextPrompts()
    {
        GoldenReport().ToSummary().ReplaceLineEndings("\n").ShouldBe(
            "Stopped at its limit of 2 repair rounds with 1 finding still open.\n"
            + "\n"
            + "Open findings:\n"
            + "- [high] A race in Parse (Lib/A.cs) - A failing test on branch:abc1234.\n"
            + "\n"
            + "Suggested next prompts:\n"
            + "1. In Lib/A.cs: A race in Parse. Take the lock.");
    }

    [Fact]
    public void TheSummary_ShowsOnlyAFewFindings_AndSaysHowManyMoreAreInTheFile()
    {
        var open = Enumerable.Range(1, 8)
            .Select(i => new ReportFinding($"F1.{i}", "j1", TeamFindingSeverity.Medium, "correctness", $"Problem {i}", [], null, null, null, FindingStatus.Open, 1, null))
            .ToList();
        var report = new PipelineReport(false, "NoProgress", 3, 2, TeamFindingSeverity.Medium, open, [], 0, 0, 0, [], []);

        var summary = report.ToSummary(maxFindings: 3).ReplaceLineEndings("\n");

        summary.ShouldBe(
            "Stopped early: more repair rounds were not making progress. 8 findings still open.\n"
            + "\n"
            + "Open findings:\n"
            + "- [medium] Problem 1\n"
            + "- [medium] Problem 2\n"
            + "- [medium] Problem 3\n"
            + "- ... and 5 more in report.md.");
    }

    [Fact]
    public void AQuoteThatContainsAFence_CannotBreakTheReportsOwn()
    {
        var finding = new ReportFinding(
            "F1.1", "j1", TeamFindingSeverity.High, "correctness", "A problem", [], null, "before ``` after", null, FindingStatus.Open, 1, null);
        var report = new PipelineReport(false, "LoopCapReached", 1, 1, TeamFindingSeverity.Medium, [finding], [], 0, 0, 0, [], []);

        var markdown = report.ToMarkdown();

        markdown.ShouldContain("before ''' after");
        markdown.Split("```").Length.ShouldBe(3); // exactly the one pair around the quote
    }

    [Fact]
    public void LongEvidence_AndMultiLineTitles_AreCutAndFoldedSoTheyStayReadable()
    {
        var finding = new ReportFinding(
            "F1.1", "j1", TeamFindingSeverity.High, "correctness", "A title\r\nover two lines", [], new string('e', 2_000), null, null, FindingStatus.Open, 1, null);
        var report = new PipelineReport(false, "LoopCapReached", 1, 1, TeamFindingSeverity.Medium, [finding], [], 0, 0, 0, [], []);

        var markdown = report.ToMarkdown();

        markdown.ShouldContain("### [high] A title over two lines");
        markdown.ShouldContain(new string('e', 600) + "...");
        markdown.ShouldNotContain(new string('e', 601));
    }
}
