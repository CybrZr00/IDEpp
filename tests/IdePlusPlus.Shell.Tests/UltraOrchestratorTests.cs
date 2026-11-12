using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// An orchestrated Ultra run: a lead that plans onto the board, checks in on the workers, and
/// reviews each merge. The lead's replies are scripted under the name "lead", the same way the
/// workers' are under theirs.
/// </summary>
public sealed class UltraOrchestratorTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();

    public void Dispose() => _repo.Dispose();

    private static ChatStreamEvent[] Text(string text) => [new TextDelta(text)];

    private UltraTeamRun CreateRun(int workers, Action<UltraTeamOptionsBuilder>? configure = null)
    {
        var builder = new UltraTeamOptionsBuilder();
        configure?.Invoke(builder);
        return new UltraTeamRun(
            _client,
            "chat-model",
            new UltraTeamOptions(workers, TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
            {
                StaggerDelay = TimeSpan.Zero,
                HardStopGrace = TimeSpan.FromSeconds(5),
                PreflightBuild = false,
                Orchestrated = true,
                // Long enough that no check-in happens unless a test calls CheckInAsync itself.
                SuperviseInterval = TimeSpan.FromHours(1),
                WorkerModel = builder.WorkerModel,
                WorkerEffort = builder.WorkerEffort,
                OrchestratorModel = builder.OrchestratorModel,
                OrchestratorEffort = builder.OrchestratorEffort,
            },
            _repo.ReadOnlyTools,
            new GitService(),
            _repo.TeamGit,
            TimeProvider.System,
            Path.Combine(_repo.Root, "ultra"),
            runCommand: null);
    }

    private sealed class UltraTeamOptionsBuilder
    {
        public string? WorkerModel { get; set; }
        public ChatEffort? WorkerEffort { get; set; }
        public string? OrchestratorModel { get; set; }
        public ChatEffort OrchestratorEffort { get; set; } = ChatEffort.High;
    }

    private Task<UltraTeamResult> RunAsync(UltraTeamRun run) => run.RunAsync(
        "Make Class1.Value return 2.",
        _repo.RepositoryRoot,
        Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
        [],
        CancellationToken.None);

    [Fact]
    public async Task TheLeadsPlan_IsOnTheBoardAsTasks_BeforeAnyWorkerStarts_AndWorkersAreToldALeadExists()
    {
        _client
            .Script("lead",
                _client.Call(LeadTools.SubmitPlanToolName, new
                {
                    tasks = new[] { "Make Class1.Value return 2 (Lib/Class1.cs)", "Cover it with a test" },
                    notes = "Keep the public surface unchanged.",
                }),
                Text("Planned."))
            .Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var run = CreateRun(workers: 1);

        await RunAsync(run);

        run.Board.Snapshot().Where(e => e.Type == TeamBoardEntryType.Task)
            .Select(e => (e.Author, e.Text))
            .ShouldBe([("lead", "Make Class1.Value return 2 (Lib/Class1.cs)"), ("lead", "Cover it with a test")]);
        run.Orchestrator!.TasksPlanned.ShouldBe(2);

        // The lead planned first; the worker's first request already sees the plan and knows of a lead.
        _client.Requests.First().Worker.ShouldBe("lead");
        var worker = _client.Requests.First(r => r.Worker == "w1").Messages;
        worker[0].Content.ShouldContain("A lead (\"lead\") has already split the goal");
        worker[1].Content.ShouldContain("Backlog: 2 open or claimed, 0 done");
        run.Bus.ChannelSnapshot().ShouldContain(m => m.From == "lead" && m.Text.Contains("public surface"));
    }

    [Fact]
    public async Task AnUnorchestratedRun_HasNoLead_AndItsWorkersAreToldThereIsNone()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        var run = new UltraTeamRun(
            _client, "chat-model",
            new UltraTeamOptions(1, TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
            {
                StaggerDelay = TimeSpan.Zero,
                PreflightBuild = false,
            },
            _repo.ReadOnlyTools, new GitService(), _repo.TeamGit, TimeProvider.System, Path.Combine(_repo.Root, "ultra"), runCommand: null);

        await RunAsync(run);

        run.Orchestrator.ShouldBeNull();
        _client.Requests.ShouldAllBe(r => r.Worker == "w1");
        _client.Requests.First().Messages[0].Content.ShouldContain("There is no lead and no orchestrator");
    }

    [Fact]
    public async Task WorkersAndTheLead_UseTheirOwnModelAndEffort()
    {
        _client
            .Script("lead",
                _client.Call(LeadTools.SubmitPlanToolName, new { tasks = new[] { "Do it" } }),
                Text("Planned."))
            .Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "done" }));
        var run = CreateRun(workers: 1, o =>
        {
            o.WorkerModel = "cheap-model";
            o.WorkerEffort = ChatEffort.Low;
            o.OrchestratorModel = "smart-model";
            o.OrchestratorEffort = ChatEffort.High;
        });

        await RunAsync(run);

        _client.Models.Where(m => m.Worker == "lead").ShouldAllBe(m => m.Model == "smart-model" && m.Effort == ChatEffort.High);
        _client.Models.Where(m => m.Worker == "w1").ShouldAllBe(m => m.Model == "cheap-model" && m.Effort == ChatEffort.Low);
    }

    [Fact]
    public async Task WithNothingConfigured_WorkersUseTheRunsModel_AndTheLeadUsesItToo()
    {
        _client
            .Script("lead", _client.Call(LeadTools.SubmitPlanToolName, new { tasks = new[] { "Do it" } }), Text("Planned."))
            .Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "done" }));
        var run = CreateRun(workers: 1);

        await RunAsync(run);

        _client.Models.ShouldAllBe(m => m.Model == "chat-model");
        _client.Models.Where(m => m.Worker == "w1").ShouldAllBe(m => m.Effort == ChatEffort.Ultra);
    }

    [Fact]
    public async Task APlanThatFails_LeavesTheWorkersToChooseForThemselves()
    {
        _client
            .Script("lead", Text("I would rather not."), Text("Still no."))
            .Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        var run = CreateRun(workers: 1);

        await RunAsync(run);

        run.Orchestrator!.TasksPlanned.ShouldBe(0);
        run.Bus.ChannelSnapshot().ShouldContain(m => m.From == "lead" && m.Text.Contains("did not produce a plan"));
        run.Board.Snapshot().ShouldNotContain(e => e.Type == TeamBoardEntryType.Task);
    }

    [Fact]
    public async Task AMergeTheLeadSendsBack_ComesBackWithItsFeedback_AndLandsOnceApproved()
    {
        _client
            .Script("lead",
                _client.Call(LeadTools.SubmitPlanToolName, new { tasks = new[] { "Make Class1.Value return 2" } }),
                Text("Planned."),
                _client.Call(LeadTools.VerdictToolName, new { approve = false, feedback = "Rename nothing; just change the value." }),
                Text("Sent back."),
                _client.Call(LeadTools.VerdictToolName, new { approve = true }),
                Text("Approved."))
            .Script("w1",
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Class1.cs", mode = "replace_text",
                    old_text = "public int Value => 1;", new_text = "public int Value => 2;",
                }),
                _client.Call(TeamTools.CommitToolName, new { message = "Value is 2" }),
                _client.Call(TeamTools.MergeToolName),
                _client.Call(TeamTools.MergeToolName),
                _client.Call(TeamTools.DoneToolName, new { summary = "merged" }));
        var run = CreateRun(workers: 1);

        var result = await RunAsync(run);

        var toolResults = _client.Requests.Where(r => r.Worker == "w1").Last().Messages
            .Where(m => m.Role == ChatRole.Tool).Select(m => m.Content).ToList();
        toolResults.ShouldContain(t => t.Contains("the lead reviewed your commit") && t.Contains("Rename nothing; just change the value."));
        toolResults.ShouldContain(t => t.StartsWith("Merged "));

        run.Orchestrator!.ReviewsRejected.ShouldBe(1);
        run.Orchestrator.ReviewsApproved.ShouldBe(1);
        result.Changes.ShouldHaveSingleItem().ShouldBe(new TeamFileChange("Lib/Class1.cs", TeamFileChangeKind.Modified));
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{result.TeamBranch}:Lib/Class1.cs").ShouldContain("Value => 2;");

        // What the lead was shown to judge: the goal, the plan, and the diff.
        var review = _client.Requests.Where(r => r.Worker == "lead").Select(r => r.Messages[1].Content)
            .First(c => c.Contains("wants to merge this"));
        review.ShouldContain("Make Class1.Value return 2");
        review.ShouldContain("+    public int Value => 2;");
    }

    // ---- the lead's calls on their own, with no git ----

    private (UltraOrchestrator Lead, TeamBoard Board, TeamMessageBus Bus) CreateLead(int maxRejections = 2)
    {
        string[] names = ["w1", "w2"];
        var workers = names.Select(n => new TeamWorkerState(n)).ToList();
        var board = new TeamBoard(TimeProvider.System, TimeSpan.FromMinutes(10));
        var bus = new TeamMessageBus(TimeProvider.System, [.. names, LeadTools.LeadName]);
        var lead = new UltraOrchestrator(
            _client, "lead-model", ChatEffort.High, _repo.ReadOnlyTools, _repo.RepositoryRoot,
            board, bus, workers, TimeProvider.System, maxRejections);
        return (lead, board, bus);
    }

    [Fact]
    public async Task AReviewWithNoVerdict_Approves()
    {
        _client.Script("lead", Text("Looks fine to me."));
        var (lead, _, _) = CreateLead();

        var (approved, feedback) = await lead.ReviewMergeAsync("goal", "w1", "diff", CancellationToken.None);

        approved.ShouldBeTrue();
        feedback.ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorkerTurnedDownTooOften_IsLetThrough_WithTheLeadsLastConcernOnTheChannel()
    {
        _client.Script("lead",
            _client.Call(LeadTools.VerdictToolName, new { approve = false, feedback = "first concern" }), Text("ok"),
            _client.Call(LeadTools.VerdictToolName, new { approve = false, feedback = "second concern" }), Text("ok"),
            _client.Call(LeadTools.VerdictToolName, new { approve = false, feedback = "third concern" }), Text("ok"));
        var (lead, _, bus) = CreateLead(maxRejections: 2);

        (await lead.ReviewMergeAsync("goal", "w1", "diff", CancellationToken.None)).Approved.ShouldBeFalse();
        (await lead.ReviewMergeAsync("goal", "w1", "diff", CancellationToken.None)).Approved.ShouldBeFalse();
        var third = await lead.ReviewMergeAsync("goal", "w1", "diff", CancellationToken.None);

        third.Approved.ShouldBeTrue();
        lead.ReviewsRejected.ShouldBe(2);
        bus.ChannelSnapshot().ShouldContain(m => m.Text.Contains("third concern"));
    }

    [Fact]
    public async Task ARejectionForOneWorker_DoesNotCountAgainstAnother()
    {
        _client.Script("lead",
            _client.Call(LeadTools.VerdictToolName, new { approve = false, feedback = "a" }), Text("ok"),
            _client.Call(LeadTools.VerdictToolName, new { approve = false, feedback = "b" }), Text("ok"));
        var (lead, _, _) = CreateLead(maxRejections: 1);

        (await lead.ReviewMergeAsync("goal", "w1", "diff", CancellationToken.None)).Approved.ShouldBeFalse();
        (await lead.ReviewMergeAsync("goal", "w2", "diff", CancellationToken.None)).Approved.ShouldBeFalse();
    }

    [Fact]
    public async Task ACheckIn_CanRedirectOneWorker_OrAllOfThem_AndAddATask()
    {
        _client.Script("lead",
            _client.Call(LeadTools.MessageToolName, new { to = "w1", text = "w2 owns the parser - leave it." }),
            _client.Call(LeadTools.MessageToolName, new { to = "all", text = "Merge small." }),
            _client.Call(LeadTools.AddTaskToolName, new { text = "Update the docs for the new value" }),
            Text("Adjusted."));
        var (lead, board, bus) = CreateLead();

        await lead.CheckInAsync("goal", TimeSpan.FromMinutes(5), CancellationToken.None);

        bus.TakeDirect("w1").Select(m => m.Text).ShouldBe(["w2 owns the parser - leave it.", "Merge small."]);
        bus.TakeDirect("w2").Select(m => m.Text).ShouldBe(["Merge small."]);
        board.Snapshot().ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            e => e.Type.ShouldBe(TeamBoardEntryType.Task),
            e => e.Author.ShouldBe("lead"),
            e => e.Text.ShouldBe("Update the docs for the new value"));
        lead.TasksPlanned.ShouldBe(1);
    }

    [Fact]
    public async Task ACheckIn_ShowsTheLeadAnyQuestionsWorkersAskedIt()
    {
        _client.Script("lead", Text("Nothing to change."));
        var (lead, _, bus) = CreateLead();
        bus.SendDirect("w2", LeadTools.LeadName, "Is the parser mine or w1's?");

        await lead.CheckInAsync("goal", TimeSpan.FromMinutes(5), CancellationToken.None);

        _client.Requests.Single().Messages[1].Content.ShouldContain("w2: Is the parser mine or w1's?");
    }

    [Fact]
    public async Task ACheckInThatFails_IsReportedToTheTeam_NotThrown()
    {
        _client.Script("lead", [new StreamError("provider fell over")]);
        var (lead, _, bus) = CreateLead();

        await lead.CheckInAsync("goal", TimeSpan.FromMinutes(5), CancellationToken.None);

        // Either way the run goes on - whether the failure surfaced as an exception or a plain reply.
        lead.Activity.ShouldBeNull();
    }
}

/// <summary>The model and effort settings the Settings window writes and the chat pane reads.</summary>
public sealed class SubAgentSettingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ideplusplus-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    private IdePlusPlus.Infrastructure.AppSettingsStore Store(params (string Key, string Value)[] values)
    {
        var store = new IdePlusPlus.Infrastructure.AppSettingsStore(_path);
        foreach (var (key, value) in values)
        {
            store.Set(key, value);
        }

        return store;
    }

    [Fact]
    public void NothingSet_MeansTheChatsOwnModelAndEffort_AndNoLead()
    {
        var store = Store();

        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentModel(store).ShouldBeNull();
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentEffort(store).ShouldBeNull();
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestrated(store).ShouldBeFalse();
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestratorModel(store).ShouldBeNull();
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestratorEffort(store).ShouldBe(ChatEffort.High);
    }

    [Fact]
    public void ConfiguredValues_AreReadBack_TrimmedAndParsed()
    {
        var store = Store(
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.SubAgentModelSettingKey, "  anthropic/claude-haiku-5-5 "),
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.SubAgentEffortSettingKey, "low"),
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.UltraOrchestratedSettingKey, "True"),
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.UltraOrchestratorModelSettingKey, "anthropic/claude-opus-5-5"),
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.UltraOrchestratorEffortSettingKey, "Medium"));

        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentModel(store).ShouldBe("anthropic/claude-haiku-5-5");
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentEffort(store).ShouldBe(ChatEffort.Low);
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestrated(store).ShouldBeTrue();
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestratorModel(store).ShouldBe("anthropic/claude-opus-5-5");
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestratorEffort(store).ShouldBe(ChatEffort.Medium);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ultra")]
    [InlineData("nonsense")]
    public void AnEffortThatIsBlankInvalidOrUltra_FallsBackToTheDefault(string stored)
    {
        var store = Store(
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.SubAgentEffortSettingKey, stored),
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.UltraOrchestratorEffortSettingKey, stored));

        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentEffort(store).ShouldBeNull();
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestratorEffort(store).ShouldBe(ChatEffort.High);
    }

    [Fact]
    public void ABlankModel_MeansTheChatsOwn()
    {
        var store = Store((IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.SubAgentModelSettingKey, "   "));

        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentModel(store).ShouldBeNull();
    }

    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("nonsense", null)]
    [InlineData("anthropic", "Anthropic")]
    [InlineData(" DeepSeek ", "DeepSeek")]
    public void AProviderSetting_IsAKnownProvider_OrNullForTheChatsOwn(string stored, string? expected)
    {
        var store = Store(
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.SubAgentProviderSettingKey, stored),
            (IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.UltraOrchestratorProviderSettingKey, stored));

        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadSubAgentProvider(store).ShouldBe(expected);
        IdePlusPlus.Shell.ToolPanes.CopilotChatPaneViewModel.ReadUltraOrchestratorProvider(store).ShouldBe(expected);
    }
}
