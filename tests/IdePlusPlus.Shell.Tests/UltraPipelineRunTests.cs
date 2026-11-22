using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The pipeline run end to end, against a real git repository and a scripted provider: the stages it
/// has so far (setup, the orchestrator's roster, the surveyors, the designer, finalizing) run in order,
/// what each agent hands in is checked and recorded under <c>_phases</c>, an agent that gives nothing is
/// a gap and not a failure, and the run cleans up after itself however it ends. The orchestrator is
/// "lead" in the script; the surveyors are u1, u2 and the designer is d1.
///
/// A designer with no script of its own hands in the plainest design (one owner, one file, no stubs), so
/// a test about another stage need not script one.
/// </summary>
public sealed class UltraPipelineRunTests : IDisposable
{
    private const string SubmitRoster = PipelineLeadTools.SubmitRosterToolName;
    private const string SubmitSurvey = PipelineStageTools.SurveySubmitToolName;
    private const string SubmitDesign = PipelineStageTools.DesignSubmitToolName;

    private const string GoodStub = "public interface IGreeter\r\n{\r\n    string Greet(string name);\r\n}\r\n";
    private const string StubMissingASemicolon = "public interface IGreeter\r\n{\r\n    string Greet(string name)\r\n}\r\n";

    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();

    public UltraPipelineRunTests()
    {
        _client.WhenScriptEmpty = worker => worker == "d1" ? PlainDesign() : [new TextDelta("Nothing more to do.")];
    }

    public void Dispose() => _repo.Dispose();

    private string UltraRoot => Path.Combine(_repo.Root, "ultra");

    private static ChatStreamEvent[] Text(string text) => [new TextDelta(text)];

    private UltraPipelineRun CreateRun(Func<UltraTeamOptions, UltraTeamOptions>? configure = null)
    {
        var options = new UltraTeamOptions(1, TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
        {
            HardStopGrace = TimeSpan.FromSeconds(5),
            PreflightBuild = false,
            OrchestratorEffort = ChatEffort.High,
        };
        return new UltraPipelineRun(
            _client, "chat-model", configure?.Invoke(options) ?? options, _repo.ReadOnlyTools, new GitService(), _repo.TeamGit,
            TimeProvider.System, UltraRoot);
    }

    private Task<UltraTeamResult> RunAsync(
        UltraPipelineRun run, CancellationToken cancellationToken = default, IReadOnlyList<string>? backlog = null) =>
        run.RunAsync(
            "Make Class1.Value return 2.",
            _repo.RepositoryRoot,
            Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            [],
            cancellationToken,
            backlog: backlog);

    /// <summary>The lead's roster: two surveyors, one on the library and one on its tests.</summary>
    private ChatStreamEvent[] TwoSurveyors() => _client.Call(SubmitRoster, new
    {
        surveyors = new[]
        {
            new { area = "Lib", question = "How is Value used?" },
            new { area = "Tests", question = "What covers it?" },
        },
    });

    /// <summary>The lead's roster for a goal that needs no surveyors - the designer is the first agent to run.</summary>
    private ChatStreamEvent[] NoSurveyors() => _client.Call(SubmitRoster, new { surveyors = Array.Empty<object>() });

    private ChatStreamEvent[] Survey(string pattern = "Value property", string file = "Lib/Class1.cs") => _client.Call(SubmitSurvey, new
    {
        summary = "Class1 has one property.",
        exists = new[] { new { pattern, file } },
    });

    // ---- What the designer does ----

    /// <summary>One owner, one existing file, no stubs: nothing to commit and nothing to land.</summary>
    private ChatStreamEvent[] PlainDesign() => _client.Call(SubmitDesign, new
    {
        summary = "Change the value in Class1.",
        owners = new[] { "b1" },
        files = new[] { new { path = "Lib/Class1.cs", owner = "b1", purpose = "Holds the value.", acceptance = new[] { "Value is 2." } } },
    });

    /// <summary>Two owners and a contract: IGreeter.cs is the stub, b1 owns it, b2 owns Class1.cs.</summary>
    private ChatStreamEvent[] ContractDesign(bool class1IsStub = false) => _client.Call(SubmitDesign, new
    {
        summary = "A greeter behind an interface.",
        owners = new[] { "b1", "b2" },
        files = new object[]
        {
            new { path = "Lib/IGreeter.cs", owner = "b1", purpose = "The contract.", acceptance = new[] { "Has Greet(string)." }, isStub = true },
            new { path = "Lib/Class1.cs", owner = "b2", purpose = "Uses it.", acceptance = new[] { "Value is 2." }, isStub = class1IsStub },
        },
        misuseCases = new[] { new { id = "M1", scenario = "A null name." } },
    });

    private ChatStreamEvent[] WriteStub(string text = GoodStub) =>
        _client.Call(TeamTools.EditFileToolName, new { file = "Lib/IGreeter.cs", mode = "whole_file", new_text = text });

    private ChatStreamEvent[] Commit(string message = "The contract") => _client.Call(TeamTools.CommitToolName, new { message });

    private async Task<T> ReadAsync<T>(UltraPipelineRun run, string path) =>
        (await new PipelineArtifactStore(run.PhasesDirectory).ReadJsonAsync<T>(path)).Value
        ?? throw new InvalidOperationException($"{path} was not written.");

    private List<string> Branches(UltraPipelineRun run) =>
        [.. TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", $"ideplusplus/ultra/{run.RunId}/*")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private int WorktreeCount() =>
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "worktree", "list", "--porcelain")
            .Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal));

    private string TeamBranch(UltraPipelineRun run) => TeamGitWorkspace.GetTeamBranchName(run.RunId);

    private string ShowOnTeamBranch(UltraPipelineRun run, string path) =>
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{TeamBranch(run)}:{path}");

    /// <summary>The tool results d1 had been sent by its last request, in order - what the runner and the dispatcher said to
    /// it. The reply to an accepted submission is never among them: the agent is stopped the moment it submits.</summary>
    private List<string> DesignerToolReplies() =>
        [.. _client.Requests.Where(r => r.Worker == "d1").Last().Messages.Where(m => m.Role == ChatRole.Tool).Select(m => m.Content)];

    // ---- The ordinary run ----

    [Fact]
    public async Task ARunThatGetsThroughItsStages_ReadsTheCodeAndRecordsWhatItFound()
    {
        _client
            .Script("lead", TwoSurveyors())
            .Script("u1", _client.Call("read_file", new { file = "Lib/Class1.cs" }), Survey())
            .Script("u2", _client.Call("list_files", new { directory = "Lib" }), Survey("Value is read in tests", "Lib/Class1.cs"));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Changes.ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        result.Workers.Select(w => (w.Name, w.Phase)).ShouldBe(
            [("u1", TeamWorkerPhase.Done), ("u2", TeamWorkerPhase.Done), ("d1", TeamWorkerPhase.Done)]);
        result.Workers.Select(w => (w.Name, w.Steps)).ShouldBe([("u1", 2), ("u2", 2), ("d1", 1)]);

        // One request for the lead, two for each surveyor and one for the designer - none spent on a reply to a submission.
        _client.Requests.Count.ShouldBe(6);
        (result.PromptTokens, result.CompletionTokens).ShouldBe((600, 60));

        var roster = await ReadAsync<RosterRecord>(run, "roster.json");
        roster.Decided.ShouldBeTrue();
        roster.Surveyors.Select(s => (s.Name, s.Area, s.Question)).ShouldBe(
            [("u1", "Lib", "How is Value used?"), ("u2", "Tests", "What covers it?")]);

        var survey = await ReadAsync<SurveyRecord>(run, "survey/u1.json");
        survey.Submitted.ShouldBeTrue();
        survey.Gap.ShouldBeNull();
        survey.Area.ShouldBe("Lib");
        survey.Report!.Exists.ShouldHaveSingleItem().File.ShouldBe("Lib/Class1.cs");

        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.EndReason.ShouldBe("AllDone");
        summary.Gaps.ShouldBeEmpty();
        summary.Stages.Select(s => s.Stage).ShouldBe(["Roster", "Understand", "Design"]);
        summary.Stages[1].Agents.Select(a => (a.Name, a.End)).ShouldBe([("u1", "Submitted"), ("u2", "Submitted")]);
        summary.PromptTokens.ShouldBe(600);

        var ownership = await ReadAsync<OwnershipRecord>(run, "ownership.json");
        ownership.Owners.ShouldBe(["b1"]);
        ownership.Files.ShouldBe(new Dictionary<string, string> { ["Lib/Class1.cs"] = "b1" });
    }

    [Fact]
    public async Task AfterARun_NothingIsLeftBehind_ButTheRecords()
    {
        _client
            .Script("lead", TwoSurveyors())
            .Script("u1", Survey())
            .Script("u2", Survey());
        var run = CreateRun();

        await RunAsync(run);

        WorktreeCount().ShouldBe(1);
        Branches(run).ShouldBeEmpty();
        Directory.Exists(Path.Combine(run.RunDirectory, "_ro")).ShouldBeFalse();
        Directory.Exists(Path.Combine(run.RunDirectory, "d1")).ShouldBeFalse();
        File.Exists(Path.Combine(run.PhasesDirectory, "summary.json")).ShouldBeTrue();
        run.StageText.ShouldBeNull();
        run.SetupStatus.ShouldBeNull();
    }

    [Fact]
    public async Task ASurveyor_ReadsTheUsersWorkingTreeAsItWasWhenTheRunStarted_NotAsItBecomes()
    {
        // Edited and not committed: the run starts from the tree as the user has it...
        var classFile = Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs");
        File.WriteAllText(classFile, "public class Class1\r\n{\r\n    public int Value => 7;\r\n}\r\n");
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1", _client.Call("read_file", new { file = "Lib/Class1.cs" }), Survey());
        var run = CreateRun();
        var edited = false;
        run.Changed += () =>
        {
            // ...and an edit made while the surveyors read does not reach them.
            if (!edited && run.StageText?.StartsWith("Understand", StringComparison.Ordinal) == true)
            {
                edited = true;
                File.WriteAllText(classFile, "public class Class1\r\n{\r\n    public int Value => 99;\r\n}\r\n");
            }
        };

        await RunAsync(run);

        edited.ShouldBeTrue();
        var read = _client.Requests.Last(r => r.Worker == "u1").Messages.Where(m => m.Role == ChatRole.Tool).First().Content;
        read.ShouldContain("Value => 7");
        read.ShouldNotContain("Value => 99");
        File.ReadAllText(classFile).ShouldContain("Value => 99"); // the user's file is theirs
    }

    [Fact]
    public async Task AnEmptyRoster_SkipsTheSurveyors_AndTheRecordSaysItWasOnPurpose()
    {
        _client.Script("lead", _client.Call(SubmitRoster, new { surveyors = Array.Empty<object>(), rationale = "One file." }));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Select(w => w.Name).ShouldBe(["d1"]);
        _client.Requests.Select(r => r.Worker).ShouldBe(["lead", "d1"]);
        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.Stages.Select(s => (s.Stage, s.Note)).ShouldBe(
            [
                ("Roster", null),
                ("Understand", "skipped: no surveyors: the designer surveys for itself"),
                ("Design", "no stubs, so no contract commit; 1 owner(s)"),
            ]);
        (await ReadAsync<RosterRecord>(run, "roster.json")).Rationale.ShouldBe("One file.");
    }

    [Fact]
    public async Task ARunThatAllowsNoExtraAgents_NeverAsksTheLead()
    {
        var run = CreateRun(o => o with { PipelineMaxUnderstand = 0, PipelineMaxCustomJudges = 0 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.ShouldAllBe(r => r.Worker == "d1");
        (await ReadAsync<RosterRecord>(run, "roster.json")).Surveyors.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheStepsTheUserApproved_ReachTheLeadAndTheDesigner_AsANumberedList()
    {
        _client.Script("lead", NoSurveyors());
        var run = CreateRun();

        await RunAsync(run, backlog: ["Change Value to 2", "Cover it with a test"]);

        _client.Requests.First(r => r.Worker == "lead").Messages[1].Content.ShouldContain("1. Change Value to 2\n2. Cover it with a test");
        _client.Requests.First(r => r.Worker == "d1").Messages[1].Content.ShouldContain("1. Change Value to 2\n2. Cover it with a test");
    }

    [Fact]
    public async Task TheLeadAndTheAgents_EachUseTheirOwnModelAndEffort()
    {
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1", Survey());
        var run = CreateRun(o => o with { OrchestratorModel = "smart-model", WorkerModel = "cheap-model" });

        await RunAsync(run);

        _client.Models.Select(m => (m.Worker, m.Model, m.Effort)).ShouldBe(
            [("lead", "smart-model", ChatEffort.High), ("u1", "cheap-model", ChatEffort.Medium), ("d1", "cheap-model", ChatEffort.High)]);
    }

    [Fact]
    public async Task AnEffortSetForWorkers_OverridesTheRolesOwn()
    {
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1", Survey());
        var run = CreateRun(o => o with { WorkerEffort = ChatEffort.Low });

        await RunAsync(run);

        _client.Models.Where(m => m.Worker is "u1" or "d1").ShouldAllBe(m => m.Effort == ChatEffort.Low);
    }

    // ---- When an agent gives nothing ----

    [Fact]
    public async Task ALeadWhoCannotDecide_LeavesAGap_AndTheRunGoesOn()
    {
        _client.Script("lead", Text("I would rather not."), Text("Still no."), Text("No."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Select(w => w.Name).ShouldBe(["d1"]);
        var roster = await ReadAsync<RosterRecord>(run, "roster.json");
        roster.Decided.ShouldBeFalse();
        roster.Note.ShouldNotBeNull().ShouldContain(SubmitRoster);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem().ShouldContain(SubmitRoster);
    }

    [Fact]
    public async Task ASurveyorWhoNeverSubmits_IsAGapInItsRecord_AndTheOtherSurveysAreKept()
    {
        _client
            .Script("lead", TwoSurveyors())
            .Script("u1", Text("There is a lot here."), Text("Still reading."), Text("Done."))
            .Script("u2", Survey());
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Select(w => (w.Name, w.Phase)).ShouldBe(
            [("u1", TeamWorkerPhase.Stopped), ("u2", TeamWorkerPhase.Done), ("d1", TeamWorkerPhase.Done)]);
        var missing = await ReadAsync<SurveyRecord>(run, "survey/u1.json");
        missing.Submitted.ShouldBeFalse();
        missing.Report.ShouldBeNull();
        missing.Gap.ShouldNotBeNull().ShouldContain(SubmitSurvey);
        (await ReadAsync<SurveyRecord>(run, "survey/u2.json")).Submitted.ShouldBeTrue();
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem().ShouldStartWith("u1:");
    }

    [Fact]
    public async Task ASurveyThatBreaksTheRules_IsSentBackWithWhy_AndAnOmittedAreaIsTheOneItWasGiven()
    {
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1",
                _client.Call(SubmitSurvey, new { exists = new[] { new { pattern = "A property", file = "" } } }),
                Survey());
        var run = CreateRun();

        await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "u1").ElementAt(1).Messages.Last().Content.ShouldContain("Not recorded:");
        _client.Requests.Where(r => r.Worker == "u1").ElementAt(1).Messages.Last().Content.ShouldContain("exists[1].file");
        var survey = await ReadAsync<SurveyRecord>(run, "survey/u1.json");
        survey.Submitted.ShouldBeTrue();
        survey.Report!.Area.ShouldBe("Lib");
    }

    [Fact]
    public async Task TheDesigner_IsGivenTheSurveysFenced_AndToldWhichOnesAreMissing()
    {
        _client
            .Script("lead", TwoSurveyors())
            .Script("u1", Survey("A property the designer should see"))
            .Script("u2", Text("Nothing."), Text("Nothing."), Text("Nothing."));
        var run = CreateRun(o => o with { PipelineMaxOwners = 3 });

        await RunAsync(run);

        var designer = _client.Requests.First(r => r.Worker == "d1").Messages;
        designer[0].Content.ShouldStartWith("You are d1, the designer");
        designer[0].Content.ShouldContain("at most 3 owners");
        designer[1].Content.ShouldContain("<<<DATA survey of Lib");
        designer[1].Content.ShouldContain("A property the designer should see");
        designer[1].Content.ShouldContain("cover those areas yourself: u2 (Tests)");
    }

    // ---- The design stage ----

    [Fact]
    public async Task TheDesigner_WritesTheContract_AndTheRunnerBuildsItAndLandsItForTwoOwners()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", _client.Call("read_file", new { file = "Lib/Class1.cs" }), WriteStub(), _client.Call(BuildTools.BuildToolName), Commit(), ContractDesign());
        var run = CreateRun();
        var readOnlyCheckoutSawTheContract = false;
        run.Changed += () =>
        {
            if (File.Exists(Path.Combine(run.RunDirectory, "_ro", "Lib", "IGreeter.cs")))
            {
                readOnlyCheckoutSawTheContract = true;
            }
        };

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Select(w => (w.Name, w.Phase)).ShouldBe([("d1", TeamWorkerPhase.Done)]);

        // The contract is on the team branch, and is the only thing the run has changed so far.
        result.TeamBranchKept.ShouldBeTrue();
        result.Changes.ShouldHaveSingleItem().ShouldBe(new TeamFileChange("Lib/IGreeter.cs", TeamFileChangeKind.Added));
        ShowOnTeamBranch(run, "Lib/IGreeter.cs").ShouldContain("string Greet(string name);");
        Branches(run).ShouldBe([TeamBranch(run)]); // d1's own branch held nothing the team branch does not
        readOnlyCheckoutSawTheContract.ShouldBeTrue();

        var design = await ReadAsync<DesignRecord>(run, "design.json");
        design.ContractCommit.ShouldBe(TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", TeamBranch(run)).Trim());
        design.Design.Files.Select(f => (f.Path, f.Owner, f.IsStub)).ShouldBe([("Lib/IGreeter.cs", "b1", true), ("Lib/Class1.cs", "b2", false)]);
        design.Design.MisuseCases.ShouldHaveSingleItem().Id.ShouldBe("M1");
        var ownership = await ReadAsync<OwnershipRecord>(run, "ownership.json");
        ownership.Owners.ShouldBe(["b1", "b2"]);
        ownership.Files.ShouldBe(new Dictionary<string, string> { ["Lib/IGreeter.cs"] = "b1", ["Lib/Class1.cs"] = "b2" });

        var stage = (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Single(s => s.Stage == "Design");
        stage.Note.ShouldNotBeNull().ShouldContain("landed: 1 stub file(s), 2 owner(s)");
    }

    [Fact]
    public async Task ADesignWithNoStubs_NeedsNoContractCommit_AndLandsNothing()
    {
        _client.Script("lead", NoSurveyors());
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Changes.ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        (await ReadAsync<DesignRecord>(run, "design.json")).ContractCommit.ShouldBeNull();
        _client.Requests.Count(r => r.Worker == "d1").ShouldBe(1); // one request: the submission, and nothing after it
    }

    [Fact]
    public async Task AContractThatDoesNotBuild_IsRefusedWithTheBuildReport_AndNothingLandsUntilItBuilds()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1",
                WriteStub(StubMissingASemicolon), Commit("A first try"), ContractDesign(),
                WriteStub(), Commit("The fix"), ContractDesign());
        var run = CreateRun();

        var result = await RunAsync(run);

        var refusal = _client.Requests.Where(r => r.Worker == "d1").ElementAt(3).Messages.Last().Content;
        refusal.ShouldStartWith("Not recorded:");
        refusal.ShouldContain("does not build");
        refusal.ShouldContain("CS1002");
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        ShowOnTeamBranch(run, "Lib/IGreeter.cs").ShouldContain("string Greet(string name);");

        // One landing: the team branch was created and moved once, onto the commit that builds.
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "reflog", "show", "--format=%H", $"refs/heads/{TeamBranch(run)}")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
    }

    [Fact]
    public async Task ACommitThatChangesAFileTheDesignDoesNotDeclareAsAStub_IsRefused_AndRevertedBeforeItLands()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1",
                WriteStub(),
                _client.Call(TeamTools.EditFileToolName, new { file = "Lib/Class1.cs", mode = "replace_text", old_text = "Value => 1", new_text = "Value => 2" }),
                Commit("Contract, and a little more"),
                ContractDesign(),
                _client.Call(TeamTools.EditFileToolName, new { file = "Lib/Class1.cs", mode = "replace_text", old_text = "Value => 2", new_text = "Value => 1" }),
                Commit("Put Class1 back"),
                ContractDesign());
        var run = CreateRun();

        var result = await RunAsync(run);

        var refusal = _client.Requests.Where(r => r.Worker == "d1").ElementAt(4).Messages.Last().Content;
        refusal.ShouldContain("Lib/Class1.cs, which the design lists but does not declare as an interface stub");
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);

        // What landed is the contract alone: Class1.cs is exactly as the user had it.
        result.Changes.ShouldHaveSingleItem().RelativePath.ShouldBe("Lib/IGreeter.cs");
        ShowOnTeamBranch(run, "Lib/Class1.cs").ShouldContain("Value => 1");
    }

    [Fact]
    public async Task AStubTheCommitDoesNotContain_IsRefused_UntilItIsWrittenAndCommitted()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", ContractDesign(), WriteStub(), Commit(), ContractDesign());
        var run = CreateRun();

        var result = await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "d1").ElementAt(1).Messages.Last().Content
            .ShouldContain("declares Lib/IGreeter.cs as an interface stub, but your commit does not change it");
        result.Changes.ShouldHaveSingleItem().RelativePath.ShouldBe("Lib/IGreeter.cs");
    }

    [Fact]
    public async Task ADesignHandedInWithUncommittedChanges_IsRefused_UntilTheyAreCommitted()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", WriteStub(), ContractDesign(), Commit(), ContractDesign());
        var run = CreateRun();

        var result = await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "d1").ElementAt(2).Messages.Last().Content.ShouldContain("uncommitted changes");
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        ShowOnTeamBranch(run, "Lib/IGreeter.cs").ShouldContain("Greet");
    }

    [Fact]
    public async Task ADesignThatBreaksTheRules_IsSentBackNamingTheFileAndTheOwners()
    {
        var twoOwnersForOneFile = _client.Call(SubmitDesign, new
        {
            summary = "Change the value.",
            owners = new[] { "b1", "b2" },
            files = new[]
            {
                new { path = "Lib/Class1.cs", owner = "b1", purpose = "Holds the value.", acceptance = new[] { "Value is 2." } },
                new { path = "Lib/Class1.cs", owner = "b2", purpose = "Holds the value.", acceptance = new[] { "Value is 2." } },
            },
        });
        _client.Script("lead", NoSurveyors()).Script("d1", twoOwnersForOneFile, PlainDesign());
        var run = CreateRun();

        var result = await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "d1").ElementAt(1).Messages.Last().Content
            .ShouldContain("Lib/Class1.cs is owned by both b1 and b2: every file has exactly one owner");
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        (await ReadAsync<OwnershipRecord>(run, "ownership.json")).Owners.ShouldBe(["b1"]);
    }

    [Fact]
    public async Task MoreOwnersThanTheRunAllows_IsSentBack()
    {
        _client.Script("lead", NoSurveyors()).Script("d1", ContractDesign(), PlainDesign());
        var run = CreateRun(o => o with { PipelineMaxOwners = 1 });

        await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "d1").ElementAt(1).Messages.Last().Content
            .ShouldContain("2 owners; at most 1 are allowed here");
    }

    [Fact]
    public async Task ADesignThatAsksForSeveralWaves_IsSentBack_ForThisRunBuildsInOne()
    {
        var waves = _client.Call(SubmitDesign, new
        {
            summary = "Change the value.",
            owners = new[] { "b1" },
            files = new[] { new { path = "Lib/Class1.cs", owner = "b1", purpose = "Holds the value.", acceptance = new[] { "Value is 2." } } },
            waves = 2,
        });
        _client.Script("lead", NoSurveyors()).Script("d1", waves, PlainDesign());
        var run = CreateRun();

        await RunAsync(run);

        _client.Requests.Where(r => r.Worker == "d1").ElementAt(1).Messages.Last().Content.ShouldContain("builds in one wave");
    }

    [Fact]
    public async Task TheDesigner_CannotMergeOrTalkToAPeer_OnlyUseTheToolsItIsGiven()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", _client.Call(TeamTools.MergeToolName), _client.Call(TeamTools.PostToolName, new { text = "hello" }), PlainDesign());
        var run = CreateRun();

        await RunAsync(run);

        var replies = DesignerToolReplies();
        replies.Count.ShouldBe(2);
        replies[0].ShouldContain("'team_merge' is not one of your tools");
        replies[1].ShouldContain("'team_post' is not one of your tools");
    }

    [Fact]
    public async Task ADesignerWhoNeverSubmits_EndsTheRun_AndWhatItCommittedIsKeptOnItsBranch()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", WriteStub(), Commit(), Text("I think it is done."), Text("Really."), Text("Yes."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.WorkersStopped);
        result.Changes.ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        result.KeptWorkerBranches.ShouldHaveSingleItem().ShouldEndWith("/d1");
        result.Workers.Select(w => (w.Name, w.Phase)).ShouldBe([("d1", TeamWorkerPhase.Stopped)]);
        File.Exists(Path.Combine(run.PhasesDirectory, "ownership.json")).ShouldBeFalse();
        File.Exists(Path.Combine(run.PhasesDirectory, "design.json")).ShouldBeFalse();

        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.EndReason.ShouldBe("WorkersStopped");
        summary.Gaps.Count.ShouldBe(2);
        summary.Gaps[0].ShouldStartWith("d1:");
        summary.Gaps[1].ShouldContain("no design");
        summary.Stages.Last().Note.ShouldBe("no design was accepted");
    }

    // ---- The run's own limits ----

    [Fact]
    public async Task ARunWhoseTokensAreSpentAfterTheRoster_DoesNotStartTheSurveyors()
    {
        _client.Script("lead", TwoSurveyors());
        var run = CreateRun(o => o with { MaxTokens = 100 }); // one request is 110

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.BudgetSpent);
        _client.Requests.ShouldAllBe(r => r.Worker == "lead");
        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.EndReason.ShouldBe("BudgetSpent");
        summary.Stages.Select(s => s.Stage).ShouldBe(["Roster"]);
        Branches(run).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARunWhoseTokensAreSpentAfterTheSurveys_DoesNotStartTheDesigner()
    {
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1", Survey());
        var run = CreateRun(o => o with { MaxTokens = 220 }); // the lead's request and the surveyor's: 220

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.BudgetSpent);
        _client.Requests.ShouldNotContain(r => r.Worker == "d1");
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Stages.Select(s => s.Stage).ShouldBe(["Roster", "Understand"]);
    }

    [Fact]
    public async Task ACancelledRun_CleansUpEverything_AndReturnsAResult()
    {
        _client
            .Script("lead", TwoSurveyors())
            .Script("u1", Survey())
            .Script("u2", Survey());
        var run = CreateRun();
        using var cancel = new CancellationTokenSource();
        run.Changed += () =>
        {
            if (run.StageText?.StartsWith("Understand", StringComparison.Ordinal) == true)
            {
                cancel.Cancel();
            }
        };

        var result = await RunAsync(run, cancel.Token);

        result.EndReason.ShouldBe(UltraTeamEndReason.Cancelled);
        result.Workers.ShouldAllBe(w => w.Phase == TeamWorkerPhase.Stopped);
        WorktreeCount().ShouldBe(1);
        Branches(run).ShouldBeEmpty();
        Directory.Exists(Path.Combine(run.RunDirectory, "_ro")).ShouldBeFalse();
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).EndReason.ShouldBe("Cancelled");
    }

    [Fact]
    public async Task ACancelledRun_DuringTheDesign_RemovesTheDesignersWorktree_AndKeepsWhatItCommitted()
    {
        _client
            .Script("lead", NoSurveyors())
            .Script("d1", WriteStub(), Commit());
        var run = CreateRun();
        using var cancel = new CancellationTokenSource();
        run.Changed += () =>
        {
            if (run.Workers.Any(w => w.Name == "d1" && w.Steps >= 2))
            {
                cancel.Cancel();
            }
        };

        var result = await RunAsync(run, cancel.Token);

        result.EndReason.ShouldBe(UltraTeamEndReason.Cancelled);
        WorktreeCount().ShouldBe(1);
        Directory.Exists(Path.Combine(run.RunDirectory, "d1")).ShouldBeFalse();
        result.KeptWorkerBranches.ShouldHaveSingleItem().ShouldEndWith("/d1"); // its commit never reached the team branch
    }

    [Fact]
    public async Task ARunWhoseWorktreesWouldNotFitOnTheDisk_IsRefusedBeforeAnythingIsCreated()
    {
        var run = CreateRun(o => o with { FreeDiskSpace = _ => 0 });

        var error = await Should.ThrowAsync<UltraTeamDiskSpaceException>(() => RunAsync(run));

        error.Message.ShouldContain("3 worktree(s) need about");
        _client.Requests.ShouldBeEmpty();
        Branches(run).ShouldBeEmpty();
        WorktreeCount().ShouldBe(1);
    }

    [Fact]
    public async Task ARecordThatCannotBeWritten_IsAGapInTheSummary_NotAFailureOfTheRun()
    {
        _client.Script("lead", NoSurveyors());
        var run = CreateRun();
        var blocked = false;
        run.Changed += () =>
        {
            // The records folder exists once setup is under way: put a folder where roster.json goes.
            if (!blocked && run.SetupStatus?.Contains("read-only copy", StringComparison.Ordinal) == true)
            {
                blocked = true;
                Directory.CreateDirectory(Path.Combine(run.PhasesDirectory, "roster.json"));
            }
        };

        var result = await RunAsync(run);

        blocked.ShouldBeTrue();
        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        (await ReadAsync<RunSummaryRecord>(run, "summary.json")).Gaps.ShouldHaveSingleItem().ShouldStartWith("could not write roster.json");
    }

    // ---- Status, as the card reads it ----

    [Fact]
    public async Task WhileItRuns_TheRunSaysWhichStageItIsIn_AndWhoHasJoined()
    {
        _client
            .Script("lead", TwoSurveyors())
            .Script("u1", Survey())
            .Script("u2", Survey());
        var run = CreateRun();
        var stages = new List<string>();
        var setups = new List<string>();
        var agentCounts = new List<int>();
        run.Changed += () =>
        {
            if (run.StageText is { } stage && !stages.Contains(stage))
            {
                stages.Add(stage);
            }

            if (run.SetupStatus is { } setup && !setups.Contains(setup))
            {
                setups.Add(setup);
            }

            agentCounts.Add(run.Workers.Count);
        };

        await RunAsync(run);

        stages.ShouldBe(
            [
                "Roster - the lead is deciding who the run needs",
                "Understand - 2 surveyors reading the code",
                "Design - d1 is planning the build",
            ]);
        setups.ShouldContain(s => s.StartsWith("Creating workspace - taking a snapshot"));
        agentCounts.Max().ShouldBe(3);
        run.MaxWorkers.ShouldBe(3);
        run.IsAutoScaling.ShouldBeFalse();
        run.Round.ShouldBe(1);
        run.TimeBudget.ShouldBe(TimeSpan.FromMinutes(10));
        run.Elapsed.ShouldBeGreaterThan(TimeSpan.Zero);
        run.PromptTokens.ShouldBe(400); // the lead's one request, each surveyor's, and the designer's
    }
}
