using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class FindingFingerprintTests
{
    [Fact]
    public void TheKeyIsStable_TwelveHexCharacters_AndIgnoresCaseAndSlashStyle()
    {
        var key = FindingFingerprint.Key("j1", "src/Clamp.cs", "Correctness");

        key.Length.ShouldBe(12);
        key.ShouldMatch("^[0-9a-f]{12}$");
        FindingFingerprint.Key("J1", @"SRC\clamp.CS", "correctness").ShouldBe(key);
        FindingFingerprint.Key("j1", "src/Clamp.cs", "Correctness").ShouldBe(key);
    }

    [Fact]
    public void TheKeyDiffersByJudgeFileAndCategory()
    {
        var key = FindingFingerprint.Key("j1", "src/Clamp.cs", "correctness");

        FindingFingerprint.Key("j2", "src/Clamp.cs", "correctness").ShouldNotBe(key);
        FindingFingerprint.Key("j1", "src/Other.cs", "correctness").ShouldNotBe(key);
        FindingFingerprint.Key("j1", "src/Clamp.cs", "security").ShouldNotBe(key);
        FindingFingerprint.Key("j1", null, "correctness").ShouldNotBe(key);
    }

    [Theory]
    [InlineData("Clamp ignores a zero value", "Clamp ignores a zero value", 1.0)]
    [InlineData("Clamp ignores a zero value", "CLAMP IGNORES A ZERO VALUE", 1.0)]
    [InlineData("Clamp ignores a zero value", "The clamp does not handle zero values", 0.6)]
    [InlineData("Missing null check in Clamp", "Clamp is missing a null check", 1.0)]
    [InlineData("Alpha beta gamma delta", "Alpha beta epsilon zeta", 0.333)]
    [InlineData("Class names mislead", "Classes mislead callers", 0.5)]
    [InlineData("Missing null check in Clamp", "Unrelated logging problem", 0.0)]
    [InlineData("", "anything", 0.0)]
    [InlineData("the a is", "also the a", 0.0)]
    public void TitleSimilarityIsTheShareOfSharedWords(string a, string b, double expected) =>
        FindingFingerprint.TitleSimilarity(a, b).ShouldBe(expected, 0.01);

    [Fact]
    public void IdenticalFillerOnlyTitlesAreStillTheSame() =>
        FindingFingerprint.TitleSimilarity("the a is", "THE A IS").ShouldBe(1.0);

    [Fact]
    public void ASameFindingNeedsTheSameKeyAndASimilarEnoughTitle()
    {
        JudgeFinding Make(string title, string file = "src/A.cs", string category = "correctness") =>
            new(title, TeamFindingSeverity.Medium, category, [file]);

        FindingFingerprint.SameFinding("j1", Make("Clamp ignores zero"), Make("Clamp ignores zero input")).ShouldBeTrue();
        FindingFingerprint.SameFinding("j1", Make("Clamp ignores zero"), Make("Something else entirely")).ShouldBeFalse();
        FindingFingerprint.SameFinding("j1", Make("Clamp ignores zero"), Make("Clamp ignores zero", file: "src/B.cs")).ShouldBeFalse();
        FindingFingerprint.SameFinding("j1", Make("Clamp ignores zero"), Make("Clamp ignores zero", category: "security")).ShouldBeFalse();
    }
}

public class FindingLedgerTests
{
    private static ValidatedFinding Open(
        string title = "Clamp ignores a zero value",
        string file = "src/Clamp.cs",
        TeamFindingSeverity severity = TeamFindingSeverity.Medium,
        string category = "correctness") =>
        new(new JudgeFinding(title, severity, category, [file], "evidence", "the quoted code here", "fix it"), FindingStatus.Open);

    private static ValidatedFinding Unverified(string title = "Clamp ignores a zero value") =>
        Open(title) with { Status = FindingStatus.Unverified, Note = "not checked" };

    [Fact]
    public void NewFindingsGetIdsByLoopAndNumber()
    {
        var ledger = new FindingLedger();

        var first = ledger.RecordRound(1, "j1", [Open("Alpha problem"), Open("Beta problem with other words", "src/B.cs")]);
        var second = ledger.RecordRound(1, "j2", [Open("Gamma maintainability", "src/C.cs", category: "maintainability")]);

        first.Select(f => f.Id).ShouldBe(["F1.1", "F1.2"]);
        second.Select(f => f.Id).ShouldBe(["F1.3"]);
        ledger.Findings.ShouldAllBe(f => f.FirstSeenLoop == 1 && f.LastSeenLoop == 1 && f.SeenCount == 1 && f.Status == FindingStatus.Open);
        ledger.Findings.Select(f => f.Judge).ShouldBe(["j1", "j1", "j2"]);
    }

    [Fact]
    public void TheSameJudgeSayingItTwiceInOneRoundIsOneFinding()
    {
        var ledger = new FindingLedger();

        ledger.RecordRound(1, "j1", [Open(), Open("Clamp ignores a zero value!")]);

        ledger.Findings.Count.ShouldBe(1);
        ledger.Findings[0].SeenCount.ShouldBe(1);
    }

    [Fact]
    public void ADifferentJudgeReportingTheSameThingIsAnotherFinding()
    {
        var ledger = new FindingLedger();

        ledger.RecordRound(1, "j1", [Open()]);
        ledger.RecordRound(1, "j3", [Open()]);

        ledger.Findings.Count.ShouldBe(2);
    }

    [Fact]
    public void AFindingReportedAgainAfterARepairIsTheSameFinding_AndIsEscalated()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open("Clamp ignores a zero value")]);
        ledger.CompleteRound(1, ["j1"]);

        var again = ledger.RecordRound(2, "j1", [Open("The clamp still ignores zero value")]);

        ledger.Findings.Count.ShouldBe(1);
        var finding = again.Single();
        finding.Id.ShouldBe("F1.1");
        finding.FirstSeenLoop.ShouldBe(1);
        finding.LastSeenLoop.ShouldBe(2);
        finding.SeenCount.ShouldBe(2);
        finding.Status.ShouldBe(FindingStatus.Escalated);
        finding.Note.ShouldNotBeNull().ShouldContain("reported again after a repair");
        finding.Finding.Title.ShouldBe("The clamp still ignores zero value"); // The latest wording.
    }

    [Fact]
    public void AFindingNotReportedAgainIsMarkedFixed_ByTheJudgeThatRaisedIt()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open("Alpha problem"), Open("Beta other problem", "src/B.cs")]);
        ledger.RecordRound(1, "j2", [Open("Gamma issue", "src/C.cs", category: "maintainability")]);
        ledger.CompleteRound(1, ["j1", "j2"]);

        ledger.RecordRound(2, "j1", [Open("Alpha problem")]); // j1 reports only Alpha again; j2 reports nothing.
        var fixedNow = ledger.CompleteRound(2, ["j1", "j2"]);

        fixedNow.Select(f => f.Id).ShouldBe(["F1.2", "F1.3"]);
        fixedNow.ShouldAllBe(f => f.Status == FindingStatus.Fixed && f.Note!.Contains("loop 2"));
        ledger.Find("F1.1")!.Status.ShouldBe(FindingStatus.Escalated);
    }

    [Fact]
    public void AJudgeThatGaveNoVerdictLeavesItsFindingsAsTheyWere()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open()]);
        ledger.CompleteRound(1, ["j1"]);

        ledger.CompleteRound(2, ["j2", "j3"]); // j1 was unavailable in loop 2.

        ledger.Findings.Single().Status.ShouldBe(FindingStatus.Open);
    }

    [Fact]
    public void AFixedFindingThatComesBackIsARegression_AndEscalated()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open()]);
        ledger.CompleteRound(1, ["j1"]);
        ledger.CompleteRound(2, ["j1"]); // Gone in loop 2: fixed.
        ledger.Find("F1.1")!.Status.ShouldBe(FindingStatus.Fixed);

        ledger.RecordRound(3, "j1", [Open()]);

        var finding = ledger.Find("F1.1")!;
        finding.Status.ShouldBe(FindingStatus.Escalated);
        finding.SeenCount.ShouldBe(2);
    }

    [Fact]
    public void AnUnverifiedFindingIsRecordedButIsNeverBlocking()
    {
        var ledger = new FindingLedger();

        ledger.RecordRound(1, "j1", [Unverified()]);

        var finding = ledger.Findings.Single();
        finding.Status.ShouldBe(FindingStatus.Unverified);
        finding.Note.ShouldBe("not checked");
        ledger.Blocking(TeamFindingSeverity.Low).ShouldBeEmpty();
        ledger.CompleteRound(1, ["j1"]).ShouldBeEmpty(); // Nothing to mark fixed: it was never open.
    }

    [Fact]
    public void AnUnverifiedReportOfAKnownFindingIsNotItComingBack()
    {
        // The code it quotes changed, so the quote no longer checks out: that is the repair working.
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open()]);
        ledger.CompleteRound(1, ["j1"]);

        ledger.RecordRound(2, "j1", [Unverified()]);
        ledger.CompleteRound(2, ["j1"]);

        var finding = ledger.Findings.Single();
        finding.Status.ShouldBe(FindingStatus.Fixed);
        finding.SeenCount.ShouldBe(1);
    }

    [Fact]
    public void AnUnverifiedFindingThatLaterChecksOutBecomesOpen()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Unverified()]);

        ledger.RecordRound(2, "j1", [Open()]);

        var finding = ledger.Findings.Single();
        finding.Status.ShouldBe(FindingStatus.Open);
        finding.SeenCount.ShouldBe(2);
    }

    [Fact]
    public void ADismissedFindingStaysDismissedWhenAJudgeRaisesItAgain()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open()]);
        ledger.SetStatus("F1.1", FindingStatus.Dismissed, "the owner's reasoning stood").ShouldBeTrue();

        ledger.RecordRound(2, "j1", [Open()]);

        var finding = ledger.Findings.Single();
        finding.Status.ShouldBe(FindingStatus.Dismissed);
        finding.Note.ShouldBe("the owner's reasoning stood");
        ledger.Blocking(TeamFindingSeverity.Low).ShouldBeEmpty();
    }

    [Fact]
    public void BlockingAndRepairableFollowStatusAndSeverity()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1",
        [
            Open("High problem alpha", "src/A.cs", TeamFindingSeverity.High),
            Open("Medium problem beta", "src/B.cs", TeamFindingSeverity.Medium),
            Open("Low problem gamma", "src/C.cs", TeamFindingSeverity.Low),
            Open("Escalated problem delta", "src/D.cs"),
            Open("Disputed problem epsilon", "src/E.cs"),
            Open("Dismissed problem zeta", "src/F.cs"),
        ]);
        ledger.SetStatus("F1.4", FindingStatus.Escalated);
        ledger.SetStatus("F1.5", FindingStatus.Disputed);
        ledger.SetStatus("F1.6", FindingStatus.Dismissed);

        ledger.Blocking(TeamFindingSeverity.Medium).Select(f => f.Id).ShouldBe(["F1.1", "F1.2", "F1.4", "F1.5"]);
        ledger.Blocking(TeamFindingSeverity.High).Select(f => f.Id).ShouldBe(["F1.1"]);
        ledger.Blocking(TeamFindingSeverity.Low).Count.ShouldBe(5);
        // Escalated and disputed ones are not handed to an owner again.
        ledger.Repairable(TeamFindingSeverity.Medium).Select(f => f.Id).ShouldBe(["F1.1", "F1.2"]);
    }

    [Fact]
    public void SetStatusOnAnUnknownIdIsFalse_AndFindIsCaseInsensitive()
    {
        var ledger = new FindingLedger();
        ledger.RecordRound(1, "j1", [Open()]);

        ledger.SetStatus("F9.9", FindingStatus.Dismissed).ShouldBeFalse();
        ledger.Find("f1.1").ShouldNotBeNull();
        ledger.Find("nope").ShouldBeNull();
        ledger.SetStatus("f1.1", FindingStatus.Disputed).ShouldBeTrue();
        ledger.Find("F1.1")!.Status.ShouldBe(FindingStatus.Disputed);
    }

    [Fact]
    public void ALedgerRestoredFromRecordsCarriesOnNumberingAndMatching()
    {
        var original = new FindingLedger();
        original.RecordRound(1, "j1", [Open("Alpha problem"), Open("Beta other problem", "src/B.cs")]);
        original.CompleteRound(1, ["j1"]);

        var restored = new FindingLedger(original.Findings);
        var added = restored.RecordRound(1, "j2", [Open("Gamma issue", "src/C.cs", category: "maintainability")]);
        var matched = restored.RecordRound(2, "j1", [Open("Alpha problem")]);

        added.Single().Id.ShouldBe("F1.3");
        matched.Single().Id.ShouldBe("F1.1");
        matched.Single().SeenCount.ShouldBe(2);
    }

    [Fact]
    public async Task ResultsArrivingFromJudgesInParallelAreAllRecorded()
    {
        var ledger = new FindingLedger();

        await Task.WhenAll(Enumerable.Range(1, 8).Select(judge => Task.Run(() =>
            ledger.RecordRound(1, $"j{judge}", Enumerable.Range(0, 5).Select(i => Open($"Problem {judge}-{i} unique{i}word", $"src/F{judge}{i}.cs", category: $"c{i}"))))));

        ledger.Findings.Count.ShouldBe(40);
        ledger.Findings.Select(f => f.Id).Distinct().Count().ShouldBe(40);
    }
}

public class ConvergenceTrackerTests
{
    private static LedgerFinding Blocking(TeamFindingSeverity severity) =>
        new("F1.1", "k", "j1", new JudgeFinding("t", severity, "c", []), 1, 1, 1, FindingStatus.Open);

    [Fact]
    public void TheScoreIsThreePerHighAndOnePerMedium_AndNothingForLow() =>
        ConvergenceTracker.Score(
            [Blocking(TeamFindingSeverity.High), Blocking(TeamFindingSeverity.High), Blocking(TeamFindingSeverity.Medium), Blocking(TeamFindingSeverity.Low)])
            .ShouldBe(7);

    [Fact]
    public void NoFindingsScoreZero() => ConvergenceTracker.Score([]).ShouldBe(0);

    [Fact]
    public void TheFirstRoundHasNothingToCompareWith()
    {
        var tracker = new ConvergenceTracker();

        tracker.Observe(9).ShouldBe(0);

        tracker.NoProgress.ShouldBeFalse();
        tracker.History.ShouldBe([9]);
    }

    [Fact]
    public void AFallingScoreIsProgress_AndClearsEarlierStalls()
    {
        var tracker = new ConvergenceTracker();
        tracker.Observe(9);
        tracker.Observe(9).ShouldBe(1);

        tracker.Observe(5).ShouldBe(0);

        tracker.NoProgress.ShouldBeFalse();
    }

    [Fact]
    public void TwoRoundsInARowThatDoNotImproveAreNoProgress()
    {
        var tracker = new ConvergenceTracker();
        tracker.Observe(6);

        tracker.Observe(6).ShouldBe(1);
        tracker.NoProgress.ShouldBeFalse();

        tracker.Observe(8).ShouldBe(2); // Worse counts as not improving.
        tracker.NoProgress.ShouldBeTrue();
    }

    [Fact]
    public void ARoundThatImprovesBetweenTwoStallsMeansTheyAreNotInARow()
    {
        var tracker = new ConvergenceTracker();
        tracker.Observe(6);
        tracker.Observe(6);
        tracker.Observe(4);
        tracker.Observe(4);

        tracker.Stalled.ShouldBe(1);
        tracker.NoProgress.ShouldBeFalse();
    }

    [Fact]
    public void TheStallLimitIsConfigurable_AndAtLeastOne()
    {
        var impatient = new ConvergenceTracker(stallLimit: 1);
        impatient.Observe(3);
        impatient.Observe(3);
        impatient.NoProgress.ShouldBeTrue();

        var zero = new ConvergenceTracker(stallLimit: 0);
        zero.Observe(3);
        zero.NoProgress.ShouldBeFalse();
        zero.Observe(3);
        zero.NoProgress.ShouldBeTrue();
    }
}
