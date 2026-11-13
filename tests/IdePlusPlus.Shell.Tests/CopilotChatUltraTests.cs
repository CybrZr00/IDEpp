using System.Net;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using IdePlusPlus.Build;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Anthropic;
using IdePlusPlus.Copilot.DeepSeek;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Mcp;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.ProjectSystem;
using IdePlusPlus.Publish;
using IdePlusPlus.Roslyn;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.SourceControl;
using IdePlusPlus.Templates;
using IdePlusPlus.TestRunner;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// Ultra effort through the real chat pane: a message sent at Ultra runs a team (one worker here, so
/// the scripted provider can answer it in order) against a real git repository, and what the team
/// merged comes back as edit proposals - never as a change to the user's files.
/// </summary>
public sealed class CopilotChatUltraTests : IDisposable
{
    private const string Done = "data: [DONE]\n\n";
    private const string Stop = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" + Done;

    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedHttp _http = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _repo.Dispose();
    }

    private static string Text(string text) =>
        $"data: {JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content = text } } } })}\n\n" + Stop;

    private static string ToolCall(string name, object arguments)
    {
        var chunk = new
        {
            choices = new[]
            {
                new
                {
                    delta = new
                    {
                        tool_calls = new[]
                        {
                            new
                            {
                                index = 0,
                                id = $"call_{Guid.NewGuid():N}",
                                type = "function",
                                function = new { name, arguments = JsonSerializer.Serialize(arguments) },
                            },
                        },
                    },
                },
            },
        };
        return $"data: {JsonSerializer.Serialize(chunk)}\n\n"
               + "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n" + Done;
    }

    [Fact]
    public void AnUltraMessage_RunsATeam_AndOffersWhatItMergedAsEditsToReview() => RunOnUiThread(async () =>
    {
        string[] script =
        [
            ToolCall(TeamTools.BoardWriteToolName, new { type = "CLAIM", text = "Class1.Value -> 2" }),
            ToolCall(TeamTools.EditFileToolName, new
            {
                file = "Lib/Class1.cs", mode = "replace_text",
                old_text = "public int Value => 1;", new_text = "public int Value => 2;",
            }),
            ToolCall(TeamTools.CommitToolName, new { message = "Value is 2" }),
            ToolCall(TeamTools.MergeToolName, new { }),
            ToolCall(TeamTools.BoardWriteToolName, new
            {
                type = "PATCH_SUMMARY", text = "files=Lib/Class1.cs | idea=return 2 | evidence=build ok",
            }),
            ToolCall(TeamTools.DoneToolName, new { summary = "Value returns 2" }),
        ];
        _http.Script = n => n <= script.Length ? script[n - 1] : Text("done");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.IsStreaming.ShouldBeFalse();
        var card = viewModel.Messages.OfType<TeamRunViewModel>().ShouldHaveSingleItem();
        card.IsRunning.ShouldBeFalse();
        card.KeptBranchesText.ShouldNotBeNull();

        var summary = viewModel.Messages.OfType<ChatMessageViewModel>().Last().Text;
        summary.ShouldContain("**Ultra team** - finished");
        summary.ShouldContain("w1: files=Lib/Class1.cs");

        var proposal = viewModel.Messages.OfType<EditProposalViewModel>().ShouldHaveSingleItem();
        proposal.IsComplete.ShouldBeTrue();
        proposal.IsDecided.ShouldBeFalse();
        proposal.FilePath.Replace('\\', '/').ShouldBe("Lib/Class1.cs");

        // Proposed, not applied: the user's file is unchanged until they accept.
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
        _http.FirstRequestBody.ShouldContain("You are w1, one of 1 equal workers");
    });

    [Fact]
    public void AnUltraMessage_OutsideAGitRepository_IsAnsweredByASingleAgent_AndSaysWhy() => RunOnUiThread(async () =>
    {
        var plainDirectory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ideplusplus-nogit-{Guid.NewGuid():N}")).FullName;
        try
        {
            var solution = Path.Combine(plainDirectory, "S.slnx");
            File.WriteAllText(solution, "<Solution />");
            _http.Script = _ => Text("single agent answer");
            var viewModel = CreateViewModel(workers: 1, solution, plainDirectory);
            viewModel.SelectedEffort = ChatEffort.Ultra;

            viewModel.InputText = "Do the thing.";
            await viewModel.SendCommand.ExecuteAsync(null);

            viewModel.Messages.OfType<TeamRunViewModel>().ShouldBeEmpty();
            viewModel.Messages.OfType<ChatMessageViewModel>().Last().Text.ShouldContain("single agent answer");
            viewModel.StatusMessage.ShouldNotBeNull();
            viewModel.StatusMessage.ShouldContain("git repository");
            _http.FirstRequestBody.ShouldContain("\"effort\":\"high\"");
        }
        finally
        {
            try
            {
                Directory.Delete(plainDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    });

    /// <summary>The same command line is a different command in a worktree the team has been
    /// writing to, so an approval from the user's own session does not carry into a run - while
    /// inside the run one approval covers it, as the user chose.</summary>
    [Fact]
    public void ShellApprovals_FromTheUsersSession_DoNotCarryIntoATeamRun() => RunOnUiThread(async () =>
    {
        var run = new { command = "echo hi", reason = "check" };
        string[] script =
        [
            ToolCall("run_command", run),
            Text("ok"),
            ToolCall("run_command", run),
            ToolCall("run_command", run),
            ToolCall(TeamTools.DoneToolName, new { summary = "nothing to change" }),
        ];
        _http.Script = n => n <= script.Length ? script[n - 1] : Text("done");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.AreShellCommandsEnabled = true;
        var prompts = new List<string>();
        viewModel.ShellCommandApprovalPrompt = (_, reason) =>
        {
            prompts.Add(reason);
            return Task.FromResult(true);
        };

        viewModel.SelectedEffort = ChatEffort.High;
        viewModel.InputText = "Say hi.";
        await viewModel.SendCommand.ExecuteAsync(null);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        viewModel.InputText = "Say hi as a team.";
        await viewModel.SendCommand.ExecuteAsync(null);

        prompts.Count.ShouldBe(2);
        prompts[1].ShouldStartWith("[Ultra w1] check");
        prompts[1].ShouldContain("worktree");
    });

    [Fact]
    public void AllowAllForThisRun_SilencesTheTeamsLaterCommands_ButNotTheNextRun() => RunOnUiThread(async () =>
    {
        string[] script =
        [
            ToolCall("run_command", new { command = "echo one", reason = "first" }),
            ToolCall("run_command", new { command = "echo two", reason = "second" }),
            ToolCall(TeamTools.DoneToolName, new { summary = "nothing to change" }),
            Text("done"),
            ToolCall("run_command", new { command = "echo three", reason = "next run" }),
            ToolCall(TeamTools.DoneToolName, new { summary = "nothing to change" }),
        ];
        _http.Script = n => n <= script.Length ? script[n - 1] : Text("done");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.AreShellCommandsEnabled = true;
        viewModel.SelectedEffort = ChatEffort.Ultra;
        var prompted = new List<string>();
        viewModel.TeamShellCommandApprovalPrompt = (command, _) =>
        {
            prompted.Add(command);
            return Task.FromResult(ApprovalChoice.AllowAll);
        };

        viewModel.InputText = "First run.";
        await viewModel.SendCommand.ExecuteAsync(null);
        viewModel.InputText = "Second run.";
        await viewModel.SendCommand.ExecuteAsync(null);

        // One prompt per run: "echo two" ran unasked after Allow all, and the next run asked again.
        prompted.ShouldBe(["echo one", "echo three"]);
        viewModel.Messages.OfType<TeamRunViewModel>().First().ShellNote.ShouldNotBeNull();
    });

    [Fact]
    public void StartFrom_OffersEarlierTeamBranches_AndARunPickedFromOneCarriesOnFromIt() => RunOnUiThread(async () =>
    {
        var earlier = TeamGitWorkspace.GetTeamBranchName("earlier");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "-c", earlier);
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Earlier.cs"), "public class Earlier { }\n");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "an earlier run's work");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "main");
        _http.Script = n => n == 1 ? ToolCall(TeamTools.DoneToolName, new { summary = "already there" }) : Text("done");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;

        await viewModel.RefreshUltraStartOptionsAsync();
        viewModel.UltraStartOptions.ShouldBe([CopilotChatPaneViewModel.WorkingTreeStartOption, earlier]);
        viewModel.UltraStartFrom.ShouldBe(CopilotChatPaneViewModel.WorkingTreeStartOption);

        viewModel.UltraStartFrom = earlier;
        viewModel.InputText = "Carry on.";
        await viewModel.SendCommand.ExecuteAsync(null);

        // The body is JSON, which escapes the backticks around the branch name.
        _http.FirstRequestBody.ShouldContain("carries on from");
        _http.FirstRequestBody.ShouldContain(earlier);
        viewModel.Messages.OfType<TeamRunViewModel>().ShouldHaveSingleItem().Header.ShouldContain($"carrying on from {earlier}");
        viewModel.Messages.OfType<EditProposalViewModel>().ShouldHaveSingleItem().FilePath.Replace('\\', '/').ShouldBe("Lib/Earlier.cs");
        // This run's own team branch is now on offer for the next one, newest first.
        viewModel.UltraStartOptions.Count.ShouldBe(3);
        viewModel.UltraStartOptions[2].ShouldBe(earlier);
    });

    [Fact]
    public void StartFrom_ABranchALaterRunCarriedOnFrom_AsksFirst_AndCanCarryOnFromTheLaterOne() => RunOnUiThread(async () =>
    {
        var (older, later) = CommitOlderAndLaterTeamBranches();
        _http.Script = n => n == 1 ? ToolCall(TeamTools.DoneToolName, new { summary = "already there" }) : Text("done");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        var asked = new List<(string Chosen, string Later, int LaterCommits)>();
        viewModel.LaterTeamBranchPrompt = (chosen, laterBranch, laterCommits) =>
        {
            asked.Add((chosen, laterBranch, laterCommits));
            return UltraStartChoice.StartFromLater;
        };

        await viewModel.RefreshUltraStartOptionsAsync();
        viewModel.UltraStartFrom = older;
        viewModel.InputText = "Carry on.";
        await viewModel.SendCommand.ExecuteAsync(null);

        asked.ShouldBe([(older, later, 1)]);
        viewModel.UltraStartFrom.ShouldBe(later);
        viewModel.Messages.OfType<TeamRunViewModel>().ShouldHaveSingleItem().Header.ShouldContain($"carrying on from {later}");
        viewModel.Messages.OfType<EditProposalViewModel>()
            .Select(p => p.FilePath.Replace('\\', '/'))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ShouldBe(["Lib/Later.cs", "Lib/Older.cs"]);
    });

    [Fact]
    public void StartFrom_ABranchALaterRunCarriedOnFrom_CancelledAtThePrompt_StartsNothing() => RunOnUiThread(async () =>
    {
        var (older, later) = CommitOlderAndLaterTeamBranches();
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        viewModel.LaterTeamBranchPrompt = (_, _, _) => UltraStartChoice.Cancel;

        await viewModel.RefreshUltraStartOptionsAsync();
        viewModel.UltraStartFrom = older;
        viewModel.InputText = "Carry on.";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.IsStreaming.ShouldBeFalse();
        viewModel.Messages.OfType<TeamRunViewModel>().ShouldBeEmpty();
        viewModel.Messages.OfType<ChatMessageViewModel>().Last().Text.ShouldContain($"not started. `{later}` already carries on");
        _http.FirstRequestBody.ShouldBeEmpty();
        Directory.Exists(viewModel.UltraRootDirectory).ShouldBeFalse();
        viewModel.UltraStartFrom.ShouldBe(older);
    });

    /// <summary>An earlier run's team branch, and a later run's that carried on from it with one
    /// commit of its own on top.</summary>
    private (string Older, string Later) CommitOlderAndLaterTeamBranches()
    {
        var older = TeamGitWorkspace.GetTeamBranchName("older");
        var later = TeamGitWorkspace.GetTeamBranchName("later");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "-c", older);
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Older.cs"), "public class Older { }\n");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "an older run's work");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "-c", later);
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Later.cs"), "public class Later { }\n");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "the later run's work on top");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "main");
        return (older, later);
    }

    /// <summary>Until done, nobody is there to review the team's files one by one, so what it merged
    /// lands on the checked-out branch as a merge commit instead of as proposals.</summary>
    [Fact]
    public void RunUntilDone_MergesTheTeamsWorkIntoTheCheckedOutBranch_InsteadOfProposingIt() => RunOnUiThread(async () =>
    {
        string[] script =
        [
            ToolCall(TeamTools.EditFileToolName, new
            {
                file = "Lib/Class1.cs", mode = "replace_text",
                old_text = "public int Value => 1;", new_text = "public int Value => 2;",
            }),
            ToolCall(TeamTools.CommitToolName, new { message = "Value is 2" }),
            ToolCall(TeamTools.MergeToolName, new { }),
            ToolCall(TeamTools.DoneToolName, new { summary = "Value returns 2" }),
        ];
        _http.Script = n => n <= script.Length ? script[n - 1] : Text("done");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        viewModel.IsUltraRunUntilDone = true;
        var before = TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", "HEAD").Trim();

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.OfType<TeamRunViewModel>().ShouldHaveSingleItem().Header.ShouldEndWith(", running until done");
        viewModel.Messages.OfType<EditProposalViewModel>().ShouldBeEmpty();
        viewModel.Messages.OfType<ChatMessageViewModel>().Last().Text.ShouldContain("Merged into `main`");
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 2;");
        var parents = TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ');
        parents.Length.ShouldBe(3);
        parents[1].ShouldBe(before);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "log", "-1", "--format=%s").ShouldStartWith("Merge Ultra run ");
        CreateViewModel(workers: 1).IsUltraRunUntilDone.ShouldBeTrue();
    });

    [Fact]
    public void ComposeTeamGoal_CarriesTheRecentConversation_CappedAndLabelled()
    {
        var earlier = new List<CommunityToolkit.Mvvm.ComponentModel.ObservableObject>
        {
            new ChatMessageViewModel(ChatRole.User) { Text = "We need a cache for the parser." },
            new ChatMessageViewModel(ChatRole.Assistant) { Text = new string('x', 2_000) },
        };

        var goal = CopilotChatPaneViewModel.ComposeTeamGoal("Now build it.", earlier);

        goal.ShouldStartWith("Now build it.");
        goal.ShouldContain("User: We need a cache for the parser.");
        goal.ShouldContain("Assistant: " + new string('x', 600) + " [...]");
        CopilotChatPaneViewModel.ComposeTeamGoal("Alone.", []).ShouldBe("Alone.");
    }

    // --- The build check before an Ultra message ---

    private void BreakTheBuild() =>
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"), "public class Class1 { public int Value => ; }");

    [Fact]
    public void AnUltraMessage_OnASolutionThatDoesNotBuild_IsAskedAboutFirst_AndCancelKeepsTheMessageAndSpendsNothing() => RunOnUiThread(async () =>
    {
        BreakTheBuild();
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        string? reported = null;
        viewModel.UltraBuildFailedPrompt = report =>
        {
            reported = report;
            return UltraBuildChoice.Cancel;
        };

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        reported.ShouldNotBeNull().ShouldContain("Build FAILED");
        viewModel.Messages.ShouldBeEmpty();
        viewModel.InputText.ShouldBe("Make Class1.Value return 2.");
        viewModel.StatusMessage.ShouldNotBeNull().ShouldContain("Ultra not started");
        viewModel.IsStreaming.ShouldBeFalse();
        _http.FirstRequestBody.ShouldBeEmpty("no model should have been called");
    });

    [Fact]
    public void AnUltraMessage_InPlanMode_OnASolutionThatDoesNotBuild_CanFixTheBuildFirst_WithEditsAvailable() => RunOnUiThread(async () =>
    {
        BreakTheBuild();
        _http.Script = _ => Text("I will fix it.");
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        viewModel.IsPlanMode = true;
        viewModel.UltraBuildFailedPrompt = _ => UltraBuildChoice.FixFirst;

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.OfType<TeamRunViewModel>().ShouldBeEmpty();
        viewModel.Messages.OfType<ChatMessageViewModel>().First().Text.ShouldBe("Fix the build before the Ultra run");

        // Plan Mode would hand this turn propose_plan and no way to edit, which is no use for fixing.
        _http.FirstRequestBody.ShouldContain("Fix the build errors first");
        _http.FirstRequestBody.ShouldContain(ProposeEditTool.ToolName);
        _http.FirstRequestBody.ShouldNotContain(ProposePlanTool.ToolName);

        // The message waits in the input box for after the fix, and Plan Mode is still on for it.
        viewModel.InputText.ShouldBe("Make Class1.Value return 2.");
        viewModel.IsPlanMode.ShouldBeTrue();
        viewModel.IsStreaming.ShouldBeFalse();
    });

    [Fact]
    public void AnUltraMessage_OnASolutionThatBuilds_IsNotAskedAboutAnything_AndTheTeamRuns() => RunOnUiThread(async () =>
    {
        _http.Script = _ => ToolCall(TeamTools.DoneToolName, new { summary = "nothing to do" });
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        viewModel.UltraBuildFailedPrompt = _ => throw new InvalidOperationException("the solution builds, so nobody should be asked");

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.OfType<TeamRunViewModel>().ShouldHaveSingleItem();
    });

    [Fact]
    public void AnUltraMessage_WhenTheUserGoesAheadAnyway_StartsTheTeam() => RunOnUiThread(async () =>
    {
        BreakTheBuild();
        var viewModel = CreateViewModel(workers: 1);
        viewModel.SelectedEffort = ChatEffort.Ultra;
        viewModel.UltraBuildFailedPrompt = _ => UltraBuildChoice.Continue;

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        // The team's own preflight build then refuses it, with the copy-versus-tree explanation.
        var text = viewModel.Messages.OfType<ChatMessageViewModel>().Last().Text;
        text.ShouldContain("**Ultra team** - didn't start");
        text.ShouldContain("If it builds in your own tree");
    });

    // --- Where the team keeps its worktrees ---

    [Fact]
    public void TheUltraWorkFolderSetting_MovesTheRunsFolderThere_AndTheDefaultIsNotTouched() => RunOnUiThread(async () =>
    {
        var custom = Path.Combine(_repo.Root, "elsewhere", "scratch");
        _http.Script = _ => ToolCall(TeamTools.DoneToolName, new { summary = "nothing to do" });
        var viewModel = CreateViewModel(workers: 1, configure: settings => settings.Set(CopilotChatPaneViewModel.UltraWorkFolderSettingKey, custom));
        viewModel.SelectedEffort = ChatEffort.Ultra;

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        viewModel.Messages.OfType<TeamRunViewModel>().ShouldHaveSingleItem();
        Directory.GetDirectories(custom).ShouldHaveSingleItem("the run's folder is made inside the chosen one");
        Directory.Exists(Path.Combine(_repo.Root, "ultra")).ShouldBeFalse("nothing goes to the default folder");
    });

    [Theory]
    [InlineData(@"relative\scratch", "needs to be a full path")]
    [InlineData(@"REPO\scratch", "is inside the repository")]
    public void AnUltraWorkFolderThatCannotBeUsed_StopsTheRunBeforeAnythingStarts_AndSaysWhy(string folder, string reason) => RunOnUiThread(async () =>
    {
        folder = folder.Replace("REPO", _repo.RepositoryRoot);
        var viewModel = CreateViewModel(workers: 1, configure: settings => settings.Set(CopilotChatPaneViewModel.UltraWorkFolderSettingKey, folder));
        viewModel.SelectedEffort = ChatEffort.Ultra;

        viewModel.InputText = "Make Class1.Value return 2.";
        await viewModel.SendCommand.ExecuteAsync(null);

        var text = viewModel.Messages.OfType<ChatMessageViewModel>().Last().Text;
        text.ShouldContain("**Ultra team** - didn't start");
        text.ShouldContain(reason);
        text.ShouldContain("Settings");
        viewModel.Messages.OfType<TeamRunViewModel>().ShouldBeEmpty();
        _http.FirstRequestBody.ShouldBeEmpty("no model should have been called");
        Directory.Exists(Path.Combine(_repo.Root, "ultra")).ShouldBeFalse();
    });

    private CopilotChatPaneViewModel CreateViewModel(
        int workers, string? solutionPath = null, string? solutionDirectory = null, Action<AppSettingsStore>? configure = null)
    {
        SolutionLoader.EnsureMSBuildRegistered();

        var appSettings = new AppSettingsStore(Path.Combine(_repo.Root, "settings.json"));
        appSettings.Set(CopilotChatPaneViewModel.UltraWorkerCountSettingKey, workers.ToString());
        configure?.Invoke(appSettings);
        var secrets = new SecureSettingsStore(Path.Combine(_repo.Root, "secrets.json"));
        secrets.SetSecret(OpenRouterChatClient.ApiKeySettingName, "test-key");

        var httpClient = new HttpClient(_http) { Timeout = Timeout.InfiniteTimeSpan };
        _disposables.Add(httpClient);

        var editService = new EditApplicationService(new EditorWorkspace(Path.Combine(_repo.Root, "recent-files.json")));
        var buildService = new BuildService();
        var roslyn = new RoslynWorkspaceService();
        _disposables.Add(roslyn);
        var fileReader = new WorkspaceFileReader();
        var index = new CodebaseIndexService(new LmStudioEmbeddingClient(httpClient, appSettings, secrets));

        var viewModel = new CopilotChatPaneViewModel(
            new OpenRouterChatClient(httpClient, secrets),
            new LmStudioChatClient(httpClient, appSettings, secrets),
            new DeepSeekChatClient(httpClient, secrets),
            new AnthropicChatClient(httpClient, secrets),
            appSettings,
            editService,
            new ProjectMemoryStore(),
            new SkillStore(),
            new ChatHistoryStore(),
            index,
            new McpServerManager(
                new McpServerConfigStore(Path.Combine(_repo.Root, "mcp-servers.json")),
                new McpProjectTrustStore(Path.Combine(_repo.Root, "mcp-trust.json"))),
            new PluginChatToolRegistry(),
            new GitService(),
            buildService,
            new PublishService(buildService),
            new TestRunService(),
            roslyn,
            fileReader,
            new ReadOnlyToolDispatcher(fileReader, index, roslyn, editService),
            new HookService(),
            new TemplateCatalog(),
            new TemplateInstantiationService(buildService))
        {
            UltraRootDirectory = Path.Combine(_repo.Root, "ultra"),
        };

        viewModel.SetSolutionContext(
            solutionPath ?? Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            solutionDirectory ?? _repo.RepositoryRoot,
            () => "Debug");
        return viewModel;
    }

    private static void RunOnUiThread(Func<Task> test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    await test();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        thread.Join(TimeSpan.FromSeconds(120)).ShouldBeTrue("the test's UI thread did not finish within 120s - looks hung.");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>The Nth chat request gets Script(N) as its whole SSE body; anything else never answers.</summary>
    private sealed class ScriptedHttp : HttpMessageHandler
    {
        private int _chatRequests;

        public Func<int, string> Script { get; set; } = _ => Stop;

        public string FirstRequestBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal))
            {
                var n = Interlocked.Increment(ref _chatRequests);
                if (n == 1)
                {
                    FirstRequestBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Script(n), Encoding.UTF8, "text/event-stream"),
                };
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
