using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

// What the run tells the user when it ends - the report - and the history it tells its own agents between rounds - the digest.
public sealed partial class UltraPipelineRunTests
{
    private const string PassingGateLine = "builds; tests not run (no tests in this stand-in); conformity not run";

    private static string ReadRunFile(UltraPipelineRun run, string path) =>
        File.ReadAllText(Path.Combine(run.PhasesDirectory, path)).ReplaceLineEndings("\n");

    // ---- The report ----

    [Fact]
    public async Task ARunThatPasses_ReportsThat_AndWritesTheReportAndTheDigest()
    {
        _client.Script("lead", NoSurveyors());
        var run = CreateRun();

        var result = await RunAsync(run);

        var report = result.Pipeline.ShouldNotBeNull();
        (report.Passed, report.EndReason, report.JudgeRounds, report.RepairRounds).ShouldBe((true, "AllDone", 1, 0));
        report.Open.ShouldBeEmpty();
        report.NextPrompts.ShouldBeEmpty();
        ReadRunFile(run, "report.md").ShouldContain("Passed: every judge was satisfied after 1 judge round and 0 repair rounds.");
        ReadRunFile(run, "loop-1/digest.md").ShouldBe($"Round 1: gate: {PassingGateLine}; judges: 4 pass, 0 fail; still open: 0 (score 0).\n");
    }

    [Fact]
    public async Task ARunThatStopsAtItsCap_ReportsWhatIsStillOpen_WithItsSeverityAndEvidence_AndWhatToAskNext()
    {
        UltraPipelineRun? run = null;
        RepairOverride = NoteRepair(() => run!, []);
        var wrong = FindingFor("high", "Value is wrong", "Make Value return 2.");
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Wrong.", [wrong]), Verdict("fail", "Still wrong.", [wrong]));
        run = CreateRun(o => o with { PipelineMaxLoops = 1 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached);
        var report = result.Pipeline.ShouldNotBeNull();
        (report.Passed, report.EndReason, report.JudgeRounds, report.RepairRounds).ShouldBe((false, "LoopCapReached", 2, 1));
        var open = report.Open.ShouldHaveSingleItem();
        (open.Id, open.Judge, open.Severity, open.Title, open.Evidence, open.Status, open.SeenCount).ShouldBe(
            ("F1.1", "j1", TeamFindingSeverity.High, "Value is wrong", "Read the file.", FindingStatus.Escalated, 2));
        report.NextPrompts.ShouldBe(["In Lib/Class1.cs: Value is wrong. Make Value return 2."]);
        report.ToSummary().ShouldContain("Stopped at its limit of 1 repair round with 1 finding still open.");

        var file = ReadRunFile(run, "report.md");
        file.ShouldContain("### [high] Value is wrong");
        file.ShouldContain("F1.1 - found by j1, escalated, seen in 2 rounds. Files: Lib/Class1.cs.");
        file.ShouldContain("Evidence: Read the file.");
        file.ShouldContain("1. In Lib/Class1.cs: Value is wrong. Make Value return 2.");
    }

    [Fact]
    public async Task ALoopThatStopsForWantOfProgress_SaysSo_AndAFindingBelowTheBarIsListedNotRepaired()
    {
        UltraPipelineRun? run = null;
        RepairOverride = NoteRepair(() => run!, []);
        var same = Verdict("fail", "Still wrong.", [Finding("medium", "Value is wrong"), Finding("low", "The name could be clearer", quote: "")]);
        _client.Script("lead", NoSurveyors()).Script("j1", same, same, same);
        run = CreateRun(o => o with { PipelineMaxLoops = 5 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.NoProgress);
        var report = result.Pipeline.ShouldNotBeNull();
        report.Headline.ShouldBe("Stopped early: more repair rounds were not making progress. 1 finding still open.");
        report.Open.ShouldHaveSingleItem().Title.ShouldBe("Value is wrong");
        report.Lower.ShouldHaveSingleItem().Title.ShouldBe("The name could be clearer");
        ReadRunFile(run, "report.md").ShouldContain("## Below the bar (not repaired)");
    }

    [Fact]
    public async Task AFindingLeftToTheUser_IsInTheReportsNextPrompts_AndTheRunStillPasses()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Out of scope.", [FindingFor("medium", "Value should come from settings", "Read it from settings.", fixBy: "user")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var report = result.Pipeline.ShouldNotBeNull();
        report.Passed.ShouldBeTrue();
        report.Open.ShouldBeEmpty();
        report.Dismissed.ShouldBe(1);
        report.NextPrompts.ShouldBe(["In Lib/Class1.cs: Value should come from settings. Read it from settings."]);
        ReadRunFile(run, "report.md").ShouldContain("1. In Lib/Class1.cs: Value should come from settings. Read it from settings.");
    }

    [Fact]
    public async Task ARunThatNeverReachedItsJudges_HasNoReport()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", Text("I think it is done."), Text("Really."), Text("Yes."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.WorkersStopped);
        result.Pipeline.ShouldBeNull();
        File.Exists(Path.Combine(run.PhasesDirectory, "report.md")).ShouldBeFalse();
    }

    // ---- The digest ----

    [Fact]
    public async Task TheDigest_TellsTheNextRound_WhatTheOwnersReported_AndWhatWasSetAside_ButNoJudgesFindings()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("b1",
                Report("Nothing to change."),
                SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning3), Commit("Class1 returns 3"), MergeToTheTeam(), Report("Returns 3."))
            .Script("j1", Verdict("fail", "Two things.",
            [
                FindingFor("medium", "Value is wrong", "Make Value return 3."),
                FindingFor("medium", "Value should come from settings", "Read it from settings.", fixBy: "user", files: "Lib/Class1.cs"),
            ]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        ReadRunFile(run, "loop-1/digest.md").ShouldBe(
            $"Round 1: gate: {PassingGateLine}; judges: 3 pass, 1 fail; still open: 2 (score 2).\n"
            + "  b1 repaired for F1.1: Returns 3.\n"
            + "  left to the user: Value should come from settings\n");

        // Round 2: every judge is told, and none is told what any judge found.
        foreach (var judge in new[] { "j1", "j2", "j3", "j4" })
        {
            var second = _client.Requests.Where(r => r.Worker == judge).ElementAt(1).Messages[1].Content;
            second.ShouldContain("<<<DATA history");
            second.ShouldContain("b1 repaired for F1.1: Returns 3.");
            second.ShouldContain("left to the user: Value should come from settings");
            if (judge != "j1")
            {
                second.ShouldNotContain("Value is wrong"); // a finding of j1's: only j1 is shown it
            }
        }

        ReadRunFile(run, "loop-2/digest.md").ShouldContain("Round 2: gate:");
    }

    [Fact]
    public async Task AnOwnerRepairingAgain_IsToldWhatItDidLastTime()
    {
        UltraPipelineRun? run = null;
        _client
            .Script("lead", NoSurveyors(), LeadTriage(new
            {
                finding = "F1.1", action = "assign", owner = "b1", instruction = "Do not patch the literal: make Value come from a constant.", reason = "The first repair only patched it.",
            }))
            .Script("b1",
                EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(), Report("Class1 returns 2."),
                SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning3), Commit("Class1 returns 3"), MergeToTheTeam(), Report("Patched the literal to 3."),
                SyncWithTheTeam(), EditFile("Lib/Class1.cs", Class1Returning4), Commit("Class1 returns 4"), MergeToTheTeam(), Report("Returns 4."))
            .Script("j1",
                Verdict("fail", "Wrong.", [FindingFor("medium", "Value is wrong", "Make Value return 3.", quote: "public int Value => 2;")]),
                Verdict("fail", "Still wrong.", [FindingFor("medium", "Value is wrong", "Make Value return 3.", quote: "public int Value => 3;")]));
        run = CreateRun();

        await RunAsync(run);

        var opening = RepairOpeningOf("b1", nth: 1);
        opening.ShouldContain("<<<DATA history");
        opening.ShouldContain("Round 1: gate:");
        opening.ShouldContain("b1 repaired for F1.1: Patched the literal to 3.");
    }
}
