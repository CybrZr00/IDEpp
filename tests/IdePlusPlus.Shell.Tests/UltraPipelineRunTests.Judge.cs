using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

// The judge stage. With no scripts of its own every judge passes at once (see the constructor); these tests script the ones they are about.
// The code under judgement is Lib/Class1.cs, whose line "public int Value => 1;" is the one a finding can quote.
public sealed partial class UltraPipelineRunTests
{
    private const string ValueLine = "public int Value => 1;";

    private ChatStreamEvent[] Verdict(string verdict, string summary = "Checked it.", object[]? findings = null, object[]? coverage = null) =>
        _client.Call(SubmitVerdict, new { verdict, summary, findings, coverage });

    private static object Finding(
        string severity, string title, string quote = ValueLine, string file = "Lib/Class1.cs", string evidence = "Read the file.") =>
        new { title, severity, category = "correctness", files = new[] { file }, evidence, quote, fix = "Change it." };

    private async Task<VerdictRecord> VerdictOfAsync(UltraPipelineRun run, string judge, int round = 1) =>
        await ReadAsync<VerdictRecord>(run, $"loop-{round}/verdicts/{judge}.json");

    // ---- What the judges say ----

    [Fact]
    public async Task EachJudge_GivesItsVerdict_RecordedOnItsOwn_AndAFailingOneKeepsTheRunFromPassing()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Value is never checked.", [Finding("medium", "Value is not validated")]))
            .Script("j2", Verdict("pass", "Readable enough.", [Finding("low", "The name could be clearer", quote: "")]));
        var run = CreateRun(o => o with { PipelineMaxLoops = 0 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached); // until the repair loop exists, a failing round is the end
        var j1 = await VerdictOfAsync(run, "j1");
        (j1.Available, j1.Verdict, j1.Summary, j1.Title).ShouldBe((true, JudgeVerdictKind.Fail, "Value is never checked.", "Gaps and flaws judge"));
        var finding = j1.Findings.ShouldHaveSingleItem();
        (finding.Status, finding.Finding.Title, finding.Finding.Severity).ShouldBe(
            (FindingStatus.Open, "Value is not validated", TeamFindingSeverity.Medium));
        finding.Finding.Files.ShouldBe(["Lib/Class1.cs"]);
        var j2 = await VerdictOfAsync(run, "j2");
        (j2.Verdict, j2.Findings.Single().Finding.Severity).ShouldBe((JudgeVerdictKind.Pass, TeamFindingSeverity.Low));

        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.EndReason.ShouldBe("LoopCapReached");
        summary.Stages.Single(s => s.Stage == "Judge").Note.ShouldBe("4 judge(s): 3 pass, 1 fail, 0 unavailable");
    }

    [Fact]
    public async Task AFindingWhoseQuoteIsNotInTheFile_IsKeptButUnverified_AndDoesNotKeepTheRunFromPassing()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "A race.", [Finding("high", "A race in Value", quote: "lock (_gate) { return _value; }")]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var finding = (await VerdictOfAsync(run, "j1")).Findings.ShouldHaveSingleItem();
        finding.Status.ShouldBe(FindingStatus.Unverified);
        finding.Note.ShouldNotBeNull().ShouldContain("quoted code was not found");
    }

    [Fact]
    public async Task AQuoteIsCheckedAgainstTheTeamBranchAsItIsNow_AndTheJudgesAreShownTheDesignTheGateAndTheChanges()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("b1", EditFile("Lib/Class1.cs", Class1Returning2), Commit("Class1 returns 2"), MergeToTheTeam(), Report("Class1 returns 2."))
            .Script("j1", Verdict("fail", "Now 2.", [Finding("medium", "Value is 2", quote: "public int Value => 2;")]))
            .Script("j2", Verdict("fail", "Stale.", [Finding("medium", "Value is 1", quote: ValueLine)]));
        var run = CreateRun(o => o with { PipelineMaxLoops = 0 });

        var result = await RunAsync(run);

        (await VerdictOfAsync(run, "j1")).Findings.Single().Status.ShouldBe(FindingStatus.Open);
        var stale = (await VerdictOfAsync(run, "j2")).Findings.Single();
        stale.Status.ShouldBe(FindingStatus.Unverified); // that line is not in the file any more
        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached); // j1's verified finding holds it up

        var opening = _client.Requests.First(r => r.Worker == "j1").Messages[1].Content;
        opening.ShouldContain("<<<DATA design");
        opening.ShouldContain("The gate: builds; tests not run (no tests in this stand-in); conformity not run.");
        opening.ShouldContain("<<<DATA changes");
        opening.ShouldContain("+    public int Value => 2;"); // what the owner merged, against the snapshot
        opening.ShouldEndWith("Judge the work, then call verdict_submit.");
        _client.Requests.First(r => r.Worker == "j1").Messages[0].Content.ShouldStartWith("You are j1, a judge");
    }

    [Fact]
    public async Task AJudge_NeverSeesAnotherJudgesFindings()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Found it.", [Finding("medium", "UNIQUE-TITLE-ONLY-J1-KNOWS")]));
        var run = CreateRun();

        await RunAsync(run);

        _client.Requests.Where(r => r.Worker is "j2" or "j3" or "j4").SelectMany(r => r.Messages)
            .ShouldNotContain(m => m.Content.Contains("UNIQUE-TITLE-ONLY-J1-KNOWS"));
    }

    // ---- Verdicts that are refused, and judges that give none ----

    [Fact]
    public async Task AVerdictThatBreaksTheRules_IsSentBackWithWhy_AndTheJudgeTriesAgain()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j2",
                Verdict("pass", "Fine.", [Finding("medium", "Something")]), // a pass cannot carry a medium finding
                Verdict("pass", "Fine after all."));
        var run = CreateRun();

        var result = await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "j2").ElementAt(1).Messages.Last().Content.ShouldContain("A PASS cannot carry a finding at medium or above");
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        (await VerdictOfAsync(run, "j2")).Summary.ShouldBe("Fine after all.");
    }

    [Fact]
    public async Task AVerdictWithNoVerdictWord_IsRefused_NotReadAsAPass()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", _client.Call(SubmitVerdict, new { summary = "I looked." }), Verdict("fail", "Wrong.", [Finding("medium", "Value is wrong")]));
        var run = CreateRun(o => o with { PipelineMaxLoops = 0 });

        var result = await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "j1").ElementAt(1).Messages.Last().Content.ShouldContain("'verdict' is required");
        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached);
    }

    [Fact]
    public async Task AJudgeWhoseVerdictKeepsBeingRefused_IsGivenUpOn_AndTheRoundGoesOnWithoutIt()
    {
        var empty = Verdict("fail", "Wrong.", findings: []); // a FAIL with no finding
        _client
            .Script("lead", NoSurveyors())
            .Script("j3", empty, empty, empty, empty);
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone); // the other three pass, and an unavailable judge holds nothing up
        _client.Requests.Count(r => r.Worker == "j3").ShouldBe(3); // not asked a fourth time
        _client.Requests.Where(r => r.Worker == "j3").Last().Messages.Last().Content.ShouldContain("A FAIL needs at least one finding"); // the second refusal; the third is never sent, the judge is stopped
        var j3 = await VerdictOfAsync(run, "j3");
        (j3.Available, j3.Verdict).ShouldBe((false, null));
        j3.Unavailable.ShouldNotBeNull().ShouldContain("refused 3 times");
        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.Gaps.ShouldHaveSingleItem().ShouldContain("j3 (Security and what-ifs judge) gave no verdict in round 1");
        summary.Stages.Single(s => s.Stage == "Judge").Note.ShouldBe("4 judge(s): 3 pass, 0 fail, 1 unavailable");
    }

    [Fact]
    public async Task AJudgeWhoNeverSubmits_IsUnavailable_NotAFailureOfTheRun()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Text("Looks fine to me."), Text("Really."), Text("Yes."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var j1 = await VerdictOfAsync(run, "j1");
        j1.Available.ShouldBeFalse();
        j1.Unavailable.ShouldNotBeNull().ShouldContain("verdict_submit");
        result.Workers.Single(w => w.Name == "j1").Phase.ShouldBe(TeamWorkerPhase.Stopped);
    }

    // ---- The security judge ----

    [Fact]
    public async Task TheSecurityJudge_MustAnswerEveryMisuseCaseTheDesignListed_AndNoOtherJudgeIsAsked()
    {
        var design = _client.Call(SubmitDesign, new
        {
            summary = "Change the value.",
            owners = new[] { "b1" },
            files = new[] { new { path = "Lib/Class1.cs", owner = "b1", purpose = "Holds the value.", acceptance = new[] { "Value is 2." } } },
            misuseCases = new[] { new { id = "M1", scenario = "A null input." }, new { id = "M2", scenario = "Two at once." } },
        });
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", design)
            .Script("j3",
                Verdict("pass", "Looks safe."),
                Verdict("pass", "Looks safe.", coverage:
                [
                    new { id = "M1", result = "covered", evidence = "Class1 takes no input." },
                    new { id = "M2", result = "partial", evidence = "No state is shared." },
                ]));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.Where(r => r.Worker == "j3").ElementAt(1).Messages.Last().Content.ShouldContain("Misuse case M1 has no answer");
        (await VerdictOfAsync(run, "j3")).Coverage.Select(c => (c.Id, c.Result)).ShouldBe([("M1", "covered"), ("M2", "partial")]);
        _client.Requests.First(r => r.Worker == "j3").Messages[1].Content.ShouldContain("Answer every misuse case in 'coverage', one entry each: M1, M2.");
        _client.Requests.First(r => r.Worker == "j1").Messages[1].Content.ShouldNotContain("Answer every misuse case");
    }

    // ---- The threshold, the custom judges, the budget ----

    [Fact]
    public async Task AHigherThreshold_LetsALowerFindingPass_AndAFailNeedsOneAtThatLevel()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j1", Verdict("fail", "Medium only.", [Finding("medium", "Value is wrong")]), Verdict("pass", "Medium is below the bar.", [Finding("medium", "Value is wrong")]));
        var run = CreateRun(o => o with { PipelineSeverityThreshold = TeamFindingSeverity.High });

        var result = await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "j1").ElementAt(1).Messages.Last().Content.ShouldContain("A FAIL needs at least one finding at high or above");
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.First(r => r.Worker == "j1").Messages[0].Content.ShouldContain("nothing at high severity or above");
    }

    [Fact]
    public async Task ACustomJudgeTheLeadAdded_RunsWithItsCharter_AndLooksWhereItWasToldFirst()
    {
        _client
            .Script("lead", _client.Call(PipelineLeadTools.SubmitRosterToolName, new
            {
                surveyors = Array.Empty<object>(),
                customJudges = new[]
                {
                    new { title = "Accessibility judge", charter = "Every control has an accessible name, and nothing relies on colour alone.", scope = "none", focusFiles = new[] { "Lib/Class1.cs" } },
                },
            }))
            .Script("j5", Verdict("pass", "Nothing to name."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var j5 = _client.Requests.First(r => r.Worker == "j5").Messages;
        j5[0].Content.ShouldStartWith("You are j5, a judge");
        j5[0].Content.ShouldContain("Every control has an accessible name");
        j5[1].Content.ShouldContain("Look at these files first: Lib/Class1.cs.");
        var record = await VerdictOfAsync(run, "j5");
        (record.Title, record.Verdict).ShouldBe(("Accessibility judge", JudgeVerdictKind.Pass));
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Single(s => s.Stage == "Judge").Note.ShouldBe("5 judge(s): 5 pass, 0 fail, 0 unavailable");
    }

    [Fact]
    public async Task ARunWhoseTokensAreSpentByTheBuild_StartsNeitherTheGateNorTheJudges()
    {
        _client.Script("lead", NoSurveyors());
        var run = CreateRun(o => o with { MaxTokens = 330 }); // the lead's request, the designer's and the owner's

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.BudgetSpent);
        _client.Requests.ShouldNotContain(r => r.Worker.StartsWith('j'));
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Select(s => s.Stage).ShouldBe(["Roster", "Understand", "Design", "Build"]);
    }

    // ---- The judge that writes tests ----

    private string AddTestsFolder()
    {
        // Only the folder is needed: a test-writing judge may write under a test project's directory, and the dispatcher knows
        // which that is from the project's path.
        Directory.CreateDirectory(Path.Combine(_repo.RepositoryRoot, "Tests"));
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Tests", "README.md"), "Tests live here.\r\n");
        return Path.Combine(_repo.RepositoryRoot, "Tests", "Tests.csproj");
    }

    private Task<UltraTeamResult> RunWithATestProjectAsync(UltraPipelineRun run, string testProject) => run.RunAsync(
        "Make Class1.Value return 2.", _repo.RepositoryRoot, Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"), [testProject], CancellationToken.None);

    [Fact]
    public async Task TheRuntimeBugsJudge_MayWriteATest_ButNotProductionCode_AndItsEvidenceStaysOnItsOwnBranch()
    {
        var testProject = AddTestsFolder();
        _client
            .Script("lead", NoSurveyors())
            .Script("j4",
                EditFile("Lib/Class1.cs", Class1Returning2),                       // production code: refused
                EditFile("Tests/JudgeTests.cs", "public class JudgeTests { }\r\n"),
                Commit("A failing test for Value"),
                Verdict("fail", "Value is wrong.", [Finding("high", "Value is wrong", evidence: "A failing test on branch:abc1234.")]));
        var run = CreateRun(o => o with { PipelineMaxLoops = 0 });

        var result = await RunWithATestProjectAsync(run, testProject);

        _client.Requests.Where(r => r.Worker == "j4").ElementAt(1).Messages.Last().Content.ShouldContain("writes tests only");
        var j4 = await VerdictOfAsync(run, "j4");
        j4.Findings.Single().Status.ShouldBe(FindingStatus.Open);
        j4.EvidenceBranch.ShouldNotBeNull().ShouldEndWith("/j4");
        j4.EvidenceCommit.ShouldBe(TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", j4.EvidenceBranch).Trim());

        // The test is on its branch, which was kept; it never reached the team branch, and the production file is untouched.
        result.KeptWorkerBranches.ShouldHaveSingleItem().ShouldBe(j4.EvidenceBranch);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{j4.EvidenceBranch}:Tests/JudgeTests.cs").ShouldContain("JudgeTests");
        result.TeamBranchKept.ShouldBeFalse(); // nothing reached the team branch, so there was nothing to keep
        result.Changes.ShouldBeEmpty();
        Branches(run).ShouldBe([j4.EvidenceBranch]);
        result.EndReason.ShouldBe(UltraTeamEndReason.LoopCapReached);
    }

    [Fact]
    public async Task AHighFindingFromTheTestWritingJudge_NeedsItsFailingTestOnItsBranch_OtherwiseItIsAMedium()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("j4",
                Verdict("fail", "Value is wrong.", [Finding("high", "Value is wrong", evidence: "I read it and it looks wrong.")]),
                Verdict("fail", "Value is wrong.", [Finding("medium", "Value is wrong", evidence: "I read it and it looks wrong.")]));
        var run = CreateRun();

        await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "j4").ElementAt(1).Messages.Last().Content
            .ShouldContain("needs a failing test committed on your own branch");
        (await VerdictOfAsync(run, "j4")).Findings.Single().Finding.Severity.ShouldBe(TeamFindingSeverity.Medium);
    }

    [Fact]
    public async Task ATestWritingJudgeWithNoRoomForItsWorktree_IsUnavailable_AndTheRunKeepsEverythingItDid()
    {
        var checks = 0;
        _client.Script("lead", NoSurveyors());
        var run = CreateRun(o => o with
        {
            // Room for the run's worktrees (the first check) and the owners' (the second); none for the judge's.
            FreeDiskSpace = _ => Interlocked.Increment(ref checks) <= 2 ? long.MaxValue / 2 : 0,
        });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone); // not refused: nothing the run did is thrown away for a judge's checkout
        var j4 = await VerdictOfAsync(run, "j4");
        j4.Available.ShouldBeFalse();
        j4.Unavailable.ShouldNotBeNull().ShouldContain("There isn't room on the disk");
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem().ShouldContain("j4 (Runtime bugs judge) gave no verdict");
        _client.Requests.ShouldNotContain(r => r.Worker == "j4");
    }
}
