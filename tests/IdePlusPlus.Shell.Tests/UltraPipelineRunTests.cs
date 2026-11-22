using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The pipeline run end to end, against a real git repository and a scripted provider: the stages it
/// has so far (setup, the orchestrator's roster, the surveyors, finalizing) run in order, what each
/// agent hands in is checked and recorded under <c>_phases</c>, an agent that gives nothing is a gap
/// and not a failure, and the run cleans up after itself however it ends. The orchestrator is "lead"
/// in the script; the surveyors are u1, u2.
/// </summary>
public sealed class UltraPipelineRunTests : IDisposable
{
    private const string SubmitRoster = PipelineLeadTools.SubmitRosterToolName;
    private const string SubmitSurvey = PipelineStageTools.SurveySubmitToolName;

    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();

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
            _client, "chat-model", configure?.Invoke(options) ?? options, _repo.ReadOnlyTools, _repo.TeamGit, TimeProvider.System, UltraRoot);
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

    private ChatStreamEvent[] Survey(string pattern = "Value property", string file = "Lib/Class1.cs") => _client.Call(SubmitSurvey, new
    {
        summary = "Class1 has one property.",
        exists = new[] { new { pattern, file } },
    });

    private async Task<T> ReadAsync<T>(UltraPipelineRun run, string path) =>
        (await new PipelineArtifactStore(run.PhasesDirectory).ReadJsonAsync<T>(path)).Value
        ?? throw new InvalidOperationException($"{path} was not written.");

    private List<string> Branches(UltraPipelineRun run) =>
        [.. TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", $"ideplusplus/ultra/{run.RunId}/*")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private int WorktreeCount() =>
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "worktree", "list", "--porcelain")
            .Split('\n').Count(line => line.StartsWith("worktree ", StringComparison.Ordinal));

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
        result.Workers.Select(w => (w.Name, w.Phase)).ShouldBe([("u1", TeamWorkerPhase.Done), ("u2", TeamWorkerPhase.Done)]);
        result.Workers.ShouldAllBe(w => w.Steps == 2);

        // One request for the lead and two for each surveyor - none spent on a reply to a submission.
        _client.Requests.Count.ShouldBe(5);
        (result.PromptTokens, result.CompletionTokens).ShouldBe((500, 50));

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
        summary.Stages.Select(s => s.Stage).ShouldBe(["Roster", "Understand"]);
        summary.Stages[1].Agents.Select(a => (a.Name, a.End)).ShouldBe([("u1", "Submitted"), ("u2", "Submitted")]);
        summary.PromptTokens.ShouldBe(500);
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
        result.Workers.ShouldBeEmpty();
        _client.Requests.ShouldAllBe(r => r.Worker == "lead");
        var summary = await ReadAsync<RunSummaryRecord>(run, "summary.json");
        summary.Stages.Select(s => (s.Stage, s.Note)).ShouldBe(
            [("Roster", null), ("Understand", "skipped: no surveyors: the designer surveys for itself")]);
        (await ReadAsync<RosterRecord>(run, "roster.json")).Rationale.ShouldBe("One file.");
    }

    [Fact]
    public async Task ARunThatAllowsNoExtraAgents_NeverAsksTheLead()
    {
        var run = CreateRun(o => o with { PipelineMaxUnderstand = 0, PipelineMaxCustomJudges = 0 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.ShouldBeEmpty();
        (await ReadAsync<RosterRecord>(run, "roster.json")).Surveyors.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheStepsTheUserApproved_ReachTheLead_AsANumberedList()
    {
        _client.Script("lead", _client.Call(SubmitRoster, new { surveyors = Array.Empty<object>() }));
        var run = CreateRun();

        await RunAsync(run, backlog: ["Change Value to 2", "Cover it with a test"]);

        _client.Requests.First().Messages[1].Content.ShouldContain("1. Change Value to 2\n2. Cover it with a test");
    }

    [Fact]
    public async Task TheLeadAndTheSurveyors_EachUseTheirOwnModelAndEffort()
    {
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1", Survey());
        var run = CreateRun(o => o with { OrchestratorModel = "smart-model", WorkerModel = "cheap-model" });

        await RunAsync(run);

        _client.Models.Select(m => (m.Worker, m.Model, m.Effort)).ShouldBe(
            [("lead", "smart-model", ChatEffort.High), ("u1", "cheap-model", ChatEffort.Medium)]);
    }

    [Fact]
    public async Task AnEffortSetForWorkers_OverridesTheSurveyorsOwn()
    {
        _client
            .Script("lead", _client.Call(SubmitRoster, new { surveyors = new[] { new { area = "Lib" } } }))
            .Script("u1", Survey());
        var run = CreateRun(o => o with { WorkerEffort = ChatEffort.Low });

        await RunAsync(run);

        _client.Models.Single(m => m.Worker == "u1").Effort.ShouldBe(ChatEffort.Low);
    }

    // ---- When an agent gives nothing ----

    [Fact]
    public async Task ALeadWhoCannotDecide_LeavesAGap_AndTheRunGoesOn()
    {
        _client.Script("lead", Text("I would rather not."), Text("Still no."), Text("No."));
        var run = CreateRun();

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.ShouldBeEmpty();
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
        result.Workers.Select(w => (w.Name, w.Phase)).ShouldBe([("u1", TeamWorkerPhase.Stopped), ("u2", TeamWorkerPhase.Done)]);
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
        _client.Script("lead", _client.Call(SubmitRoster, new { surveyors = Array.Empty<object>() }));
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

        stages.ShouldBe(["Roster - the lead is deciding who the run needs", "Understand - 2 surveyors reading the code"]);
        setups.ShouldContain(s => s.StartsWith("Creating workspace - taking a snapshot"));
        agentCounts.Max().ShouldBe(2);
        run.MaxWorkers.ShouldBe(2);
        run.IsAutoScaling.ShouldBeFalse();
        run.Round.ShouldBe(1);
        run.TimeBudget.ShouldBe(TimeSpan.FromMinutes(10));
        run.Elapsed.ShouldBeGreaterThan(TimeSpan.Zero);
        run.PromptTokens.ShouldBe(300); // the lead's one request, and each surveyor's
    }
}
