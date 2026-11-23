using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

// The loop: gate, judges and - while they are not satisfied - a repair, up to the cap. The repair itself is a stand-in here that
// changes the team branch from outside, the way an owner's merge would; what the owners do in a repair is tested with the repair.
public sealed partial class UltraPipelineRunTests
{
    private const string Class1Returning3 = "public class Class1\r\n{\r\n    public int Value => 3;\r\n}\r\n";

    /// <summary>What a test sets to be the repair stage; null leaves the run with none.</summary>
    private Func<int, IReadOnlyList<LedgerFinding>, CancellationToken, Task<RepairOutcome>>? RepairOverride { get; set; }

    /// <summary>
    /// What a repair does to the team branch, made from outside the run: a commit on top of it that writes <paramref name="content"/>
    /// to <paramref name="path"/> (relative to the repository, which is the solution folder here), as an owner's merge would leave it.
    /// </summary>
    private void AdvanceTeamBranch(UltraPipelineRun run, string path, string content, string message = "Repair")
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"ideplusplus-advance-{Guid.NewGuid():N}");
        var teamHead = TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", TeamBranch(run));
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "worktree", "add", "--detach", scratch, teamHead);
        try
        {
            File.WriteAllText(Path.Combine(scratch, path), content);
            TeamRepositoryFixture.Git(scratch, "add", "-A");
            TeamRepositoryFixture.Git(scratch, "-c", "user.name=Repair", "-c", "user.email=repair@example.com", "commit", "-m", message);
            var newHead = TeamRepositoryFixture.Git(scratch, "rev-parse", "HEAD");
            TeamRepositoryFixture.Git(_repo.RepositoryRoot, "update-ref", $"refs/heads/{TeamBranch(run)}", newHead, teamHead);
        }
        finally
        {
            TeamRepositoryFixture.Git(_repo.RepositoryRoot, "worktree", "remove", "--force", scratch);
        }
    }

    private async Task<FindingsRecord> FindingsAsync(UltraPipelineRun run, int round) =>
        await ReadAsync<FindingsRecord>(run, $"loop-{round}/findings.json");

    /// <summary>A repair that always changes something - a note, so the code the judges quote is left alone - and keeps what it was
    /// asked to fix.</summary>
    private Func<int, IReadOnlyList<LedgerFinding>, CancellationToken, Task<RepairOutcome>> NoteRepair(
        Func<UltraPipelineRun> run, List<IReadOnlyList<LedgerFinding>> asked) =>
        (round, findings, _) =>
        {
            asked.Add(findings);
            AdvanceTeamBranch(run(), "Notes.md", $"Repair {round}.\r\n", $"Repair {round}");
            return Task.FromResult(new RepairOutcome(true, $"repair {round}"));
        };

    // ---- A round that passes ----

    [Fact]
    public async Task ARoundThatPasses_IsRecordedWithNothingOpen_AndNeedsNoRepair()
    {
        _client.Script("lead", NoSurveyors());
        var repairs = 0;
        RepairOverride = (_, _, _) =>
        {
            repairs++;
            return Task.FromResult(new RepairOutcome(true));
        };
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        repairs.ShouldBe(0);
        var findings = await FindingsAsync(run, 1);
        (findings.Round, findings.Passed, findings.Score, findings.Stalled).ShouldBe((1, true, 0, 0));
        findings.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task APassIsAPass_EvenWhenTheJudgesSpentTheLastOfTheBudget()
    {
        _client.Script("lead", NoSurveyors());
        // The lead, the designer, the owner and the four judges: seven requests of 110 tokens, and no repair to leave any for.
        var run = CreateRun(o => o with { MaxTokens = 770, PipelineMaxLoops = 0 });

        var result = await RunAsync(run);

        result.PromptTokens.ShouldBe(700);
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
    }

    // ---- A round that fails, and the repair ----

    [Fact]
    public async Task AFailingRound_IsRepaired_AndTheNextRoundIsToldWhatTheRepairChangedAndWhatItFoundBefore()
    {
        UltraPipelineRun? run = null;
        var asked = new List<IReadOnlyList<LedgerFinding>>();
        RepairOverride = (round, findings, _) =>
        {
            asked.Add(findings);
            AdvanceTeamBranch(run!, "Lib/Class1.cs", Class1Returning3, "Repair: Class1 returns 3");
            return Task.FromResult(new RepairOutcome(true));
        };
        _client
            .Script("lead", NoSurveyors())
            .Script("b1", EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(), Report("Class1 returns 2."))
            .Script("j1",
                Verdict("fail", "Wrong value.", [Finding("medium", "Value is wrong", quote: "public int Value => 2;")]),
                Verdict("pass", "Fixed."));
        run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var repaired = asked.ShouldHaveSingleItem().ShouldHaveSingleItem();
        (repaired.Id, repaired.Judge, repaired.Status, repaired.Finding.Title).ShouldBe(("F1.1", "j1", FindingStatus.Open, "Value is wrong"));
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 3");

        // Round 2: judged again, and the finding it had is shown to the judge that made it - and only to that one.
        var second = _client.Requests.Where(r => r.Worker == "j1").ElementAt(1).Messages[1].Content;
        second.ShouldContain("This is round 2.");
        second.ShouldContain("<<<DATA earlier findings");
        second.ShouldContain("F1.1 [medium] Value is wrong");
        second.ShouldContain("under the same title and category");
        second.ShouldContain("+    public int Value => 3;"); // what the repair changed...
        second.ShouldNotContain("Value => 1;");                // ...and not what the build had changed already
        var other = _client.Requests.Where(r => r.Worker == "j2").ElementAt(1).Messages[1].Content;
        other.ShouldContain("This is round 2.");
        other.ShouldNotContain("earlier findings");
        other.ShouldNotContain("Value is wrong");

        var round1 = await FindingsAsync(run, 1);
        (round1.Passed, round1.Score, round1.Stalled).ShouldBe((false, 1, 0));
        round1.Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Open);
        var round2 = await FindingsAsync(run, 2);
        (round2.Passed, round2.Score).ShouldBe((true, 0));
        var fixedFinding = round2.Findings.ShouldHaveSingleItem();
        (fixedFinding.Status, fixedFinding.Note).ShouldBe((FindingStatus.Fixed, "not reported again in loop 2"));

        (await VerdictOfAsync(run, "j1", round: 2)).Verdict.ShouldBe(JudgeVerdictKind.Pass);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.TakeLast(4).Select(s => (s.Stage, s.Round)).ShouldBe(
            [("Gate", 1), ("Judge", 1), ("Gate", 2), ("Judge", 2)]);
    }

    [Fact]
    public async Task InTheSecondRound_AQuoteIsCheckedAgainstTheRepairedCode()
    {
        UltraPipelineRun? run = null;
        RepairOverride = (_, _, _) =>
        {
            AdvanceTeamBranch(run!, "Lib/Class1.cs", Class1Returning3, "Repair: Class1 returns 3");
            return Task.FromResult(new RepairOutcome(true));
        };
        _client
            .Script("lead", NoSurveyors())
            .Script("j1",
                Verdict("fail", "Wrong value.", [Finding("medium", "The value is one", quote: ValueLine)]),
                Verdict("fail", "Still wrong.", [Finding("medium", "Class1 now returns three instead of two", quote: "public int Value => 3;")]));
        run = CreateRun(o => o with { PipelineMaxLoops = 1 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached);
        var second = (await VerdictOfAsync(run, "j1", round: 2)).Findings.ShouldHaveSingleItem();
        second.Status.ShouldBe(FindingStatus.Open); // that line is only in the code after the repair
        var round2 = await FindingsAsync(run, 2);
        round2.Findings.Select(f => (f.Id, f.Status)).ShouldBe([("F1.1", FindingStatus.Fixed), ("F2.1", FindingStatus.Open)]);
    }

    [Fact]
    public async Task ARedGate_SkipsTheJudges_GoesToTheRepairWithItsErrors_AndTheJudgesComeInTheRoundAfter()
    {
        UltraPipelineRun? run = null;
        var asked = new List<IReadOnlyList<LedgerFinding>>();
        var gates = 0;
        GateOverride = (request, _) => Task.FromResult(Interlocked.Increment(ref gates) == 1 ? FailingGate(request.Commit) : PassingGate(request.Commit));
        RepairOverride = NoteRepair(() => run!, asked);
        _client.Script("lead", NoSurveyors());
        run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        gates.ShouldBe(2);
        Directory.Exists(Path.Combine(run.PhasesDirectory, "loop-1", "verdicts")).ShouldBeFalse(); // no judge ran in the red round
        _client.Requests.Count(r => r.Worker.StartsWith('j')).ShouldBe(4);                           // all four, in the green one
        var sent = asked.ShouldHaveSingleItem().ShouldHaveSingleItem();
        (sent.Judge, sent.Finding.Title, sent.Finding.Severity, sent.Finding.Category).ShouldBe(
            ("J0", "CS1002: ; expected", TeamFindingSeverity.High, "build"));

        var round1 = await FindingsAsync(run, 1);
        (round1.Passed, round1.Score).ShouldBe((false, 3));
        var round2 = await FindingsAsync(run, 2);
        round2.Passed.ShouldBeTrue();
        round2.Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Fixed);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.TakeLast(3).Select(s => (s.Stage, s.Round)).ShouldBe(
            [("Gate", 1), ("Gate", 2), ("Judge", 2)]);
    }

    [Fact]
    public async Task AJudgeWhoGivesNoVerdictInALaterRound_DoesNotClearWhatItFoundBefore()
    {
        UltraPipelineRun? run = null;
        RepairOverride = NoteRepair(() => run!, []);
        _client
            .Script("lead", NoSurveyors())
            .Script("j1",
                Verdict("fail", "Wrong.", [Finding("medium", "Value is wrong")]),
                Text("Looks fine."), Text("Really."), Text("Yes."), // round 2: nothing submitted
                Verdict("pass", "Fixed."));                          // round 3
        run = CreateRun();

        var result = await RunAsync(run);

        var round2 = await FindingsAsync(run, 2);
        round2.Passed.ShouldBeFalse(); // j1 said nothing, so its finding stands
        round2.Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Open);
        (await VerdictOfAsync(run, "j1", round: 2)).Available.ShouldBeFalse();
        (await FindingsAsync(run, 3)).Findings.ShouldHaveSingleItem().Status.ShouldBe(FindingStatus.Fixed);
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
    }

    // ---- Where the loop stops ----

    [Fact]
    public async Task TheLastRound_IsJudgedAndNotRepaired_AndAFindingThatCameBackIsEscalated()
    {
        UltraPipelineRun? run = null;
        var asked = new List<IReadOnlyList<LedgerFinding>>();
        RepairOverride = NoteRepair(() => run!, asked);
        var stays = Finding("medium", "Value is not validated");
        _client
            .Script("lead", NoSurveyors())
            .Script("j1",
                Verdict("fail", "Two things.", [stays, Finding("medium", "Nobody documents the setter")]),
                Verdict("fail", "One left.", [stays]),
                Verdict("fail", "Still there.", [stays]));
        run = CreateRun(o => o with { PipelineMaxLoops = 2 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached);
        asked.Count.ShouldBe(2); // rounds 1 and 2 are repaired; round 3 is only judged
        _client.Requests.Count(r => r.Worker == "j1").ShouldBe(3);
        var last = await FindingsAsync(run, 3);
        (last.Passed, last.Score).ShouldBe((false, 1));
        var kept = last.Findings.Single(f => f.Finding.Title == "Value is not validated");
        (kept.Status, kept.SeenCount, kept.FirstSeenLoop, kept.LastSeenLoop).ShouldBe((FindingStatus.Escalated, 3, 1, 3));
        last.Findings.Single(f => f.Finding.Title == "Nobody documents the setter").Status.ShouldBe(FindingStatus.Fixed);
    }

    [Fact]
    public async Task ALoopWhoseOpenFindingsDoNotShrink_StopsEarly_WithoutSpendingTheRestOfItsRepairs()
    {
        UltraPipelineRun? run = null;
        var asked = new List<IReadOnlyList<LedgerFinding>>();
        RepairOverride = NoteRepair(() => run!, asked);
        var same = Verdict("fail", "Still wrong.", [Finding("medium", "Value is wrong")]);
        _client.Script("lead", NoSurveyors()).Script("j1", same, same, same, same, same);
        run = CreateRun(o => o with { PipelineMaxLoops = 5 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.NoProgress);
        _client.Requests.Count(r => r.Worker == "j1").ShouldBe(3); // rounds 1, 2 and 3: two in a row without a drop
        asked.Count.ShouldBe(2);                                    // the third round was not repaired
        var third = await FindingsAsync(run, 3);
        (third.Score, third.Stalled).ShouldBe((1, 2));
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem()
            .ShouldBe("The loop stopped after round 3: what is open has not shrunk in 2 rounds in a row (score 1, 1, 1), so more repairs are not likely to help.");
    }

    [Fact]
    public async Task AFallingScore_KeepsTheLoopGoing_EvenWhenAFindingRemains()
    {
        UltraPipelineRun? run = null;
        RepairOverride = NoteRepair(() => run!, []);
        var wrong = Finding("medium", "Value is wrong");
        var also = Finding("medium", "Nobody documents the setter");
        var third = Finding("medium", "The name is unclear", file: "Lib/Class1.cs", quote: "public class Class1");
        _client
            .Script("lead", NoSurveyors())
            .Script("j1",
                Verdict("fail", "Three.", [wrong, also, third]),
                Verdict("fail", "Two.", [wrong, also]),
                Verdict("fail", "One.", [wrong]),
                Verdict("pass", "None."));
        run = CreateRun(o => o with { PipelineMaxLoops = 3 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.Count(r => r.Worker == "j1").ShouldBe(4);
        (await FindingsAsync(run, 3)).Score.ShouldBe(1);
        (await FindingsAsync(run, 4)).Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task ARepairThatChangesNothing_EndsTheLoop_AsNoProgress()
    {
        RepairOverride = (_, _, _) => Task.FromResult(new RepairOutcome(false, "no owner could fix it"));
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Wrong.", [Finding("medium", "Value is wrong")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.NoProgress);
        _client.Requests.Count(r => r.Worker == "j1").ShouldBe(1); // not judged again: the code is the same
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem()
            .ShouldBe("The loop stopped after round 1: the repair changed nothing (no owner could fix it), so judging the same code again would find the same things.");
    }

    [Fact]
    public async Task ARunOutOfBudgetBetweenTheRoundAndItsRepair_StopsThereAndRepairsNothing()
    {
        var repairs = 0;
        RepairOverride = (_, _, _) =>
        {
            repairs++;
            return Task.FromResult(new RepairOutcome(true));
        };
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Wrong.", [Finding("medium", "Value is wrong")]));
        // Seven requests of 110 tokens, then nothing left for a repair.
        var run = CreateRun(o => o with { MaxTokens = 770, PipelineMaxLoops = 1 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.BudgetSpent);
        repairs.ShouldBe(0);
    }
}
