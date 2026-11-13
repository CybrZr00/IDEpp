using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.ToolPanes;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public sealed class UltraTeamRunTests : IDisposable
{
    private readonly TeamRepositoryFixture _repo = new();
    private readonly ScriptedTeamChatClient _client = new();

    public void Dispose() => _repo.Dispose();

    private string UltraRoot => Path.Combine(_repo.Root, "ultra");

    /// <summary>Preflight is off unless a test is about it: most of these never build, and a real
    /// dotnet build per test would only make them slow.</summary>
    private UltraTeamRun CreateRun(
        int workers,
        TimeSpan budget,
        Func<CancellationToken, Task<bool>>? askToContinue = null,
        bool preflight = false,
        Func<UltraTeamOptions, UltraTeamOptions>? configure = null) => new(
        _client,
        "model-x",
        (configure ?? (o => o))(new UltraTeamOptions(workers, budget, ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
        {
            StaggerDelay = TimeSpan.Zero,
            HardStopGrace = TimeSpan.FromSeconds(5),
            PreflightBuild = preflight,
        }),
        _repo.ReadOnlyTools,
        new GitService(),
        _repo.TeamGit,
        TimeProvider.System,
        UltraRoot,
        runCommand: null,
        askToContinue);

    private Task<UltraTeamResult> RunAsync(
        UltraTeamRun run, CancellationToken cancellationToken = default, string? startFrom = null, IReadOnlyList<string>? backlog = null) =>
        run.RunAsync(
            "Make Class1.Value return 2.",
            _repo.RepositoryRoot,
            Path.Combine(_repo.RepositoryRoot, "Scratch.slnx"),
            [],
            cancellationToken,
            startFrom,
            backlog);

    private static string Worktree(UltraTeamRun run, string worker, string root) => Path.Combine(root, run.RunId, worker);

    /// <summary>A branch off main with one more file, as an earlier run's team branch would be -
    /// the repository is left on main with its files unchanged.</summary>
    private void CreateEarlierBranch(string name, string relativePath, string content)
    {
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "-c", name);
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, relativePath), content);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "earlier work");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "switch", "main");
    }

    [Fact]
    public async Task ModelRequests_NeverExceedTheTeamsLimit_HoweverManyWorkersThereAre()
    {
        foreach (var worker in new[] { "w1", "w2", "w3", "w4" })
        {
            _client.Script(worker, _client.Call(TeamTools.BoardReadToolName), _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        }

        _client.DelayPerReply = TimeSpan.FromMilliseconds(40);
        var run = CreateRun(workers: 4, budget: TimeSpan.FromMinutes(10), configure: o => o with { MaxConcurrentModelRequests = 2 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.MaxInFlight.ShouldBe(2);
    }

    /// <summary>Three failed steps in a row stop a worker, and a rate-limit burst hits many workers at
    /// once - so a refusal that arrived before any output is retried, not shown to the worker.</summary>
    [Fact]
    public async Task ARateLimitedRequest_IsRetriedWithBackoff_InsteadOfFailingTheWorkersStep()
    {
        _client.Script("w1",
            [new StreamError("OpenRouter returned 429: rate limited, slow down")],
            [new StreamError("OpenRouter returned 429: rate limited, slow down")],
            _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), configure: o => o with { RateLimitBackoff = TimeSpan.FromMilliseconds(5) });

        var result = await RunAsync(run);

        result.Workers.ShouldHaveSingleItem().IsDone.ShouldBeTrue();
        _client.Requests.Count.ShouldBe(3);
        _client.Requests.ShouldAllBe(r => r.Messages.All(m => !m.Content.Contains("[error")));
    }

    [Fact]
    public async Task ARateLimitThatOutlastsTheRetries_ReachesTheWorkerAsAnError()
    {
        _client.WhenScriptEmpty = _ => [new StreamError("OpenRouter returned 429: rate limited")];
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), configure: o => o with
        {
            RateLimitBackoff = TimeSpan.FromMilliseconds(1),
            RateLimitRetries = 2,
        });

        var result = await RunAsync(run);

        // Three errored cycles in a row, each after its own retries: 3 x (1 + 2) requests.
        result.Workers.ShouldHaveSingleItem().IsDone.ShouldBeFalse();
        _client.Requests.Count.ShouldBe(9);
    }

    /// <summary>A run left to go until done has nobody to ask whether to keep spending, so its token
    /// budget ends it outright - after warning the team to wrap up at 90%.</summary>
    [Fact]
    public async Task ATokenBudget_WarnsTheTeamAt90Percent_ThenEndsTheRun_WithoutAskingToContinue()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.UsagePerReply = (100, 10);
        var asked = false;
        var run = CreateRun(
            workers: 1,
            budget: TimeSpan.FromMinutes(10),
            askToContinue: _ =>
            {
                asked = true;
                return Task.FromResult(true);
            },
            configure: o => o with { MaxTokens = 600 });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.BudgetSpent);
        asked.ShouldBeFalse();
        // 110 tokens a reply: the fifth (550) crosses 90%, the sixth (660) spends the budget.
        _client.Requests.Count.ShouldBe(6);
        _client.Requests.Last().Messages.Last().Content.ShouldContain("90% of its token budget");
    }

    // --- Auto mode: the team starts small and grows when its workers ask ---

    [Fact]
    public async Task AutoMode_AWorkerAsksForHelp_AndNewcomersJoinTheRoundFromTheTeamBranch()
    {
        _client.Script("w1",
            _client.Call(TeamTools.RequestHelpToolName, new { count = 2, reason = "four TASKs left and little time" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        _client.Script("w2", _client.Call(TeamTools.DoneToolName, new { summary = "joined, nothing left" }));
        _client.Script("w3", _client.Call(TeamTools.DoneToolName, new { summary = "joined, nothing left" }));
        var run = CreateRun(workers: 3, budget: TimeSpan.FromMinutes(10), configure: o => o with
        {
            AutoScale = true,
            InitialWorkerCount = 1,
            HelpCooldown = TimeSpan.Zero,
        });
        run.Workers.Count.ShouldBe(1);

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.Workers.Select(w => w.Name).ShouldBe(["w1", "w2", "w3"]);
        result.Workers.ShouldAllBe(w => w.IsDone);
        run.Bus.ChannelSnapshot().ShouldContain(m =>
            m.From == TeamMessageBus.RunnerName && m.Text == "w2, w3 joined the team at w1's request: four TASKs left and little time");
        var w1Requests = _client.Requests.Where(r => r.Worker == "w1").ToList();
        w1Requests[0].Messages[0].Content.ShouldContain("The team can grow, up to 3 workers");
        w1Requests[1].Messages.Last().Content.ShouldContain("Granted: w2, w3 are joining now");
        Directory.Exists(Worktree(run, "w3", UltraRoot)).ShouldBeFalse();
    }

    [Fact]
    public async Task AutoMode_RefusesHelpWhileTheLastNewcomersAreStillStarting()
    {
        _client.Script("w1",
            _client.Call(TeamTools.RequestHelpToolName, new { count = 1, reason = "a second pair of hands" }),
            _client.Call(TeamTools.RequestHelpToolName, new { count = 1, reason = "and a third" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        _client.Script("w2", _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        var run = CreateRun(workers: 4, budget: TimeSpan.FromMinutes(10), configure: o => o with
        {
            AutoScale = true,
            InitialWorkerCount = 1,
            HelpCooldown = TimeSpan.FromHours(1),
        });

        var result = await RunAsync(run);

        result.Workers.Count.ShouldBe(2);
        var replies = ToolReplies("w1");
        replies[0].ShouldStartWith("Granted: w2 is joining now");
        replies[1].ShouldStartWith("Refused for now: the team grew");
    }

    [Fact]
    public async Task AutoMode_RefusesHelpBeyondTheTeamsLimit_AndGrantsNoMoreThanTheRoomLeft()
    {
        _client.Script("w1",
            _client.Call(TeamTools.RequestHelpToolName, new { count = 2, reason = "lots left" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        _client.Script("w2",
            _client.Call(TeamTools.RequestHelpToolName, new { count = 1, reason = "still lots left" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        var run = CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10), configure: o => o with
        {
            AutoScale = true,
            InitialWorkerCount = 1,
            HelpCooldown = TimeSpan.Zero,
        });

        var result = await RunAsync(run);

        result.Workers.Select(w => w.Name).ShouldBe(["w1", "w2"]);
        ToolReplies("w1")[0].ShouldStartWith("Granted: w2 is joining now");
        ToolReplies("w2")[0].ShouldStartWith("Refused: the team is at its limit of 2 workers");
    }

    /// <summary>What a worker's tool calls answered, in order - the last message of each of its
    /// requests after its first.</summary>
    private List<string> ToolReplies(string worker) =>
        _client.Requests.Where(r => r.Worker == worker).Skip(1)
            .Select(r => r.Messages.Last())
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content)
            .ToList();

    [Fact]
    public async Task AutoMode_RefusesHelpWhenTheDiskWillNotTakeAnotherWorktree()
    {
        var checks = 0;
        _client.Script("w1",
            _client.Call(TeamTools.RequestHelpToolName, new { count = 1, reason = "more hands" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "ok" }));
        var run = CreateRun(workers: 4, budget: TimeSpan.FromMinutes(10), configure: o => o with
        {
            AutoScale = true,
            InitialWorkerCount = 1,
            HelpCooldown = TimeSpan.Zero,
            // Room for the team it starts with, none for anyone joining.
            FreeDiskSpace = _ => Interlocked.Increment(ref checks) == 1 ? long.MaxValue / 2 : 0,
        });

        var result = await RunAsync(run);

        result.Workers.Count.ShouldBe(1);
        _client.Requests.Last().Messages.Last().Content.ShouldStartWith("Refused: the disk will not take 1 more worktree(s)");
    }

    [Fact]
    public async Task ATeamWhoseWorktreesWouldNotFitOnTheDisk_IsRefusedBeforeAnythingIsCreated()
    {
        var run = CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10), configure: o => o with { FreeDiskSpace = _ => 0 });

        var error = await Should.ThrowAsync<UltraTeamDiskSpaceException>(() => RunAsync(run));

        error.Message.ShouldContain("2 worktree(s) need about");
        _client.Requests.ShouldBeEmpty();
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", $"ideplusplus/ultra/{run.RunId}/*").ShouldBeEmpty();
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(16, 5_000)]
    [InlineData(25, 5_000)]
    [InlineData(64, 1_904)]
    public void StartsAreStaggered_ButTheLastWorkerStartsWithinTwoMinutes(int workers, int expectedStepMs)
    {
        var step = UltraTeamRun.StaggerStep(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(2), workers);

        ((int)step.TotalMilliseconds).ShouldBe(expectedStepMs);
        (step * (workers - 1)).ShouldBeLessThanOrEqualTo(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task ARunStartedFromAnEarlierBranch_HasItsWorkInEveryWorktree_AndOffersItForReview()
    {
        CreateEarlierBranch("earlier", "Lib/Earlier.cs", "public class Earlier { }\n");
        _client.Script("w1",
            _client.Call(WorkspaceTools.ReadFileToolName, new { file = "Lib/Earlier.cs" }),
            _client.Call(TeamTools.DoneToolName, new { summary = "it was already done" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run, startFrom: "earlier");

        run.StartedFrom.ShouldBe("earlier");
        var requests = _client.Requests.Select(r => r.Messages).ToList();
        requests[0][1].Content.ShouldContain("carries on from `earlier`");
        requests[1].ShouldContain(m => m.Role == ChatRole.Tool && m.Content.Contains("class Earlier"));

        // Compared against the user's tree, so the earlier branch's work is offered too.
        result.Changes.ShouldContain(new TeamFileChange("Lib/Earlier.cs", TeamFileChangeKind.Added));
        result.TeamBranchKept.ShouldBeTrue();
        File.Exists(Path.Combine(_repo.RepositoryRoot, "Lib", "Earlier.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task AnApprovedPlansSteps_AreOnTheBoardAsTasks_BeforeTheFirstWorkerLooks()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10));

        await RunAsync(run, backlog: ["Extract the dialog seam", "Port the login window"]);

        run.Board.Snapshot().Where(e => e.Type == TeamBoardEntryType.Task).Select(e => e.Text)
            .ShouldBe(["Extract the dialog seam", "Port the login window"]);
        var opening = _client.Requests.First().Messages[1].Content;
        opening.ShouldContain("Backlog: 2 open or claimed, 0 done");
        opening.ShouldContain("G2 [TASK] runner: Port the login window");
    }

    /// <summary>Three FAM runs relearned the same dead ends ("global.json pin is a dead end") because
    /// each started with an empty board.</summary>
    [Fact]
    public async Task CarryingOnFromAnEarlierTeamBranch_TakesOverItsLessonsAndUnfinishedTasks_NotItsClaims()
    {
        const string earlierRun = "20261025-151019-4058";
        var earlierBranch = TeamGitWorkspace.GetTeamBranchName(earlierRun);
        CreateEarlierBranch(earlierBranch, "Lib/Earlier.cs", "public class Earlier { }\n");
        Directory.CreateDirectory(Path.Combine(UltraRoot, earlierRun));
        var earlierBoard = new TeamBoard(TimeProvider.System, TimeSpan.FromMinutes(10), Path.Combine(UltraRoot, earlierRun, "board.jsonl"));
        earlierBoard.Write("w5", "FAIL", "global.json pin is a dead end", null);
        earlierBoard.Write("w2", "FACT", "FAM.Desktop builds clean", null);
        var (finished, _) = earlierBoard.Write("runner", "TASK", "Extract the dialog seam", null);
        earlierBoard.Write("runner", "TASK", "Port the login window", null);
        earlierBoard.Write("w3", "CLAIM", "Theme work", null);
        earlierBoard.Write("w2", "PATCH_SUMMARY", $"files=IUserDialogService.cs | idea={finished!.Id} | evidence=build ok", null);
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10));

        await RunAsync(run, startFrom: earlierBranch);

        var carried = run.Board.Snapshot();
        carried.Select(e => e.Text).ShouldBe(
        [
            "global.json pin is a dead end",
            "FAM.Desktop builds clean",
            "Port the login window",
            // The finished task it names was not carried, so its id cannot match anything here.
            $"files=IUserDialogService.cs | idea=earlier-{finished.Number} | evidence=build ok",
        ]);
        run.Board.CycleDigest("someone-new").ShouldContain("Backlog: 1 open or claimed, 0 done");
        carried.ShouldAllBe(e => e.Author.EndsWith($"(run {earlierRun})") || e.Author == "w1");
        run.CarriedBoardEntries.ShouldBe(4);
        _client.Requests.First().Messages[1].Content.ShouldContain("global.json pin is a dead end");
    }

    [Fact]
    public async Task ARunStartedFromABranchThatConflictsWithTheWorkingTree_IsRefusedBeforeAnythingIsCreated()
    {
        CreateEarlierBranch("earlier", "Lib/Class1.cs", "public class Class1\r\n{\r\n    public int Value => 2;\r\n}\r\n");
        File.WriteAllText(
            Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"),
            "public class Class1\r\n{\r\n    public int Value => 3;\r\n}\r\n");
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10));

        var error = await Should.ThrowAsync<InvalidOperationException>(() => RunAsync(run, startFrom: "earlier"));

        error.Message.ShouldContain("Lib/Class1.cs");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*").ShouldBeEmpty();
        _client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ATeamThatFinishesTheGoal_EndsEarly_WithTheChangeOnTheTeamBranch_AndCleansUp()
    {
        _client
            .Script("w1",
                _client.Call(TeamTools.BoardWriteToolName, new { type = "CLAIM", text = "Class1.Value -> 2" }),
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Class1.cs", mode = "replace_text",
                    old_text = "public int Value => 1;", new_text = "public int Value => 2;",
                }),
                _client.Call(TeamTools.CommitToolName, new { message = "Value is 2" }),
                _client.Call(TeamTools.MergeToolName),
                _client.Call(TeamTools.BoardWriteToolName, new
                {
                    type = "PATCH_SUMMARY", text = "files=Lib/Class1.cs | idea=Value 2 | evidence=build ok",
                }),
                _client.Call(TeamTools.DoneToolName, new { summary = "Value is 2" }))
            .Script("w2", _client.Call(TeamTools.DoneToolName, new { summary = "w1 has it" }));
        var run = CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        result.TeamBranchKept.ShouldBeTrue();
        result.Changes.ShouldHaveSingleItem().ShouldBe(new TeamFileChange("Lib/Class1.cs", TeamFileChangeKind.Modified));
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{result.TeamBranch}:Lib/Class1.cs").ShouldContain("Value => 2;");
        result.Board.ShouldContain(e => e.Type == TeamBoardEntryType.PatchSummary && e.Author == "w1");
        result.PromptTokens.ShouldBeGreaterThan(0);

        // The user's checkout is untouched, and nothing of the workers is left behind but the team branch.
        File.ReadAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs")).ShouldContain("Value => 1;");
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*")
            .Trim().ShouldBe(result.TeamBranch);
        File.Exists(Path.Combine(UltraRoot, run.RunId, "board.jsonl")).ShouldBeTrue();
    }

    /// <summary>Deleting every worker branch at the end used to throw away whatever had not been
    /// merged - on a run whose merge gate never passed, all of it.</summary>
    [Fact]
    public async Task WorkThatNeverReachedTheTeamBranch_IsKeptOnTheWorkersBranch_CommittedIfItWasNot()
    {
        _client
            .Script("w1",
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Committed.cs", mode = "whole_file", new_text = "public class Committed { }\n",
                }),
                _client.Call(TeamTools.CommitToolName, new { message = "committed, never merged" }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }))
            .Script("w2",
                _client.Call(TeamTools.EditFileToolName, new
                {
                    file = "Lib/Uncommitted.cs", mode = "whole_file", new_text = "public class Uncommitted { }\n",
                }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }),
                _client.Call(TeamTools.DoneToolName, new { summary = "stopping" }))
            .Script("w3", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var run = CreateRun(workers: 3, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run);

        var w1 = TeamGitWorkspace.GetWorkerBranchName(run.RunId, 1);
        var w2 = TeamGitWorkspace.GetWorkerBranchName(run.RunId, 2);
        result.KeptWorkerBranches.ShouldBe([w1, w2], ignoreOrder: true);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{w1}:Lib/Committed.cs").ShouldContain("Committed");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{w2}:Lib/Uncommitted.cs").ShouldContain("Uncommitted");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "log", "-1", "--format=%s", w2).ShouldContain("Uncommitted work");

        // w3 had nothing of its own, so its branch is gone; nothing was merged, so the team branch is too.
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", TeamGitWorkspace.GetWorkerBranchName(run.RunId, 3))
            .ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    /// <summary>Three 16-worker runs spent ~109M tokens on a solution whose every build failed on
    /// a broken .NET install; one build first would have told the user before any of it.</summary>
    [Fact]
    public async Task ASolutionThatDoesNotBuild_StopsTheRunBeforeAnyWorkerStarts()
    {
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Class1.cs"), "public class Class1 { public int Value => ; }");
        var run = CreateRun(workers: 3, budget: TimeSpan.FromMinutes(10), preflight: true);

        var error = await Should.ThrowAsync<UltraTeamPreflightException>(() => RunAsync(run));

        error.Report.ShouldContain("Build FAILED");
        _client.Requests.ShouldBeEmpty();
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*").ShouldBeEmpty();
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    /// <summary>The pattern three FAM runs showed for their whole budget: every merge build
    /// failing, nothing reaching the team branch. The run now pauses and asks instead.</summary>
    [Fact]
    public async Task ATeamWhoseMergesKeepFailing_StallsAndAsks_WithTheLatestFailure()
    {
        _client.Script("w1",
            _client.Call(TeamTools.EditFileToolName, new
            {
                file = "Lib/Class1.cs", mode = "replace_text",
                old_text = "public int Value => 1;", new_text = "public int Value => ;",
            }),
            _client.Call(TeamTools.CommitToolName, new { message = "broken" }),
            _client.Call(TeamTools.MergeToolName));
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        string? askedBecause = null;
        UltraTeamRun? run = null;
        run = new UltraTeamRun(
            _client,
            "model-x",
            new UltraTeamOptions(1, TimeSpan.FromMinutes(10), ShellCommandsEnabled: false, "Debug", ChatEffort.Ultra)
            {
                StaggerDelay = TimeSpan.Zero,
                HardStopGrace = TimeSpan.FromSeconds(5),
                PreflightBuild = false,
                StallWindow = TimeSpan.FromSeconds(2),
                StallFailedMergeBuilds = 1,
                StallCheckInterval = TimeSpan.FromMilliseconds(250),
            },
            _repo.ReadOnlyTools,
            new GitService(),
            _repo.TeamGit,
            TimeProvider.System,
            UltraRoot,
            runCommand: null,
            askToContinue: _ =>
            {
                askedBecause = run!.PauseExplanation;
                return Task.FromResult(false);
            });

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.Stalled);
        askedBecause.ShouldNotBeNull();
        askedBecause.ShouldContain("stalled");
        askedBecause.ShouldContain("Build FAILED");
        result.Workers[0].FailedMergeBuilds.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task ASolutionThatBuilds_PassesPreflight_AndTheTeamStarts()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));

        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), preflight: true));

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        _client.Requests.ShouldNotBeEmpty();
    }

    /// <summary>FamRestApi.sln lists "..\RRS.FamRetail\RRS.FamRetail.csproj", a checkout beside the repo.
    /// In a worktree under the run folder that path pointed at nothing, so restore failed with MSB3202
    /// and a run that had already spent a large plan's tokens never started.</summary>
    [Fact]
    public async Task ASolutionThatBuildsFromAFolderBesideTheRepository_StillBuildsInTheTeamsWorktree()
    {
        var dependency = Directory.CreateDirectory(Path.Combine(_repo.Root, "Dep")).FullName;
        File.WriteAllText(Path.Combine(dependency, "Dep.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(dependency, "Dep.cs"), "public static class Dep { public const int One = 1; }");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\..\Dep\Dep.csproj" />
              </ItemGroup>
            </Project>
            """);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "reference a sibling folder");
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), preflight: true);

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var opening = _client.Requests.First().Messages[1].Content;
        opening.ShouldContain("Outside the repository, the solution also builds from: `Dep`");

        // The link is only for the run: gone afterwards, with the folder it pointed at untouched.
        Directory.Exists(Path.Combine(UltraRoot, run.RunId, "Dep")).ShouldBeFalse();
        File.Exists(Path.Combine(dependency, "Dep.cs")).ShouldBeTrue();
    }

    /// <summary>The second FamRestApi failure: FamWholesale.csproj copies node_modules\bootstrap\scss\*.scss
    /// to its output, node_modules is git-ignored, and a worktree has none - 52 MSB3030 errors on files
    /// that were in the user's own tree.</summary>
    [Fact]
    public async Task ASolutionThatBuildsFromAGitIgnoredNodeModules_StillBuildsInTheTeamsWorktree_AndLeavesTheOriginalAlone()
    {
        File.AppendAllText(Path.Combine(_repo.RepositoryRoot, ".gitignore"), "node_modules/\n");
        var modules = Path.Combine(_repo.RepositoryRoot, "Lib", "node_modules", "pkg");
        Directory.CreateDirectory(modules);
        File.WriteAllText(Path.Combine(modules, "data.txt"), "from npm");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <None Include="node_modules\pkg\data.txt" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "build from node_modules");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "status", "--porcelain", "--ignored").ShouldContain("!! Lib/node_modules/");
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), preflight: true);

        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        var opening = _client.Requests.First().Messages[1].Content;
        opening.ShouldContain("git-ignored dependency folders");
        opening.ShouldContain("`Lib/node_modules`");

        // Removing the worktree must not have followed the link into the user's own folder.
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
        File.ReadAllText(Path.Combine(modules, "data.txt")).ShouldBe("from npm");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "status", "--porcelain").ShouldBeEmpty();
    }

    /// <summary>Anything else git ignores that a build needs cannot be guessed at, but it can be named:
    /// the failure says which folder is in the user's tree and not in the copy.</summary>
    [Fact]
    public async Task ASolutionThatNeedsSomeOtherGitIgnoredFolder_FailsPreflight_AndNamesIt()
    {
        File.AppendAllText(Path.Combine(_repo.RepositoryRoot, ".gitignore"), "generated/\n");
        var generated = Path.Combine(_repo.RepositoryRoot, "Lib", "generated");
        Directory.CreateDirectory(generated);
        File.WriteAllText(Path.Combine(generated, "data.txt"), "made by a tool");
        File.WriteAllText(Path.Combine(_repo.RepositoryRoot, "Lib", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <None Include="generated\data.txt" CopyToOutputDirectory="PreserveNewest" />
              </ItemGroup>
            </Project>
            """);
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "add", "-A");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "commit", "-m", "build from a generated folder");
        var run = CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), preflight: true);

        var error = await Should.ThrowAsync<UltraTeamPreflightException>(() => RunAsync(run));

        error.Report.ShouldContain("MSB3030");
        error.NotMirrored.ShouldHaveSingleItem().ShouldStartWith(@"Lib\generated (in your tree, but not in the team's copy");
        _client.Requests.ShouldBeEmpty();
        File.Exists(Path.Combine(generated, "data.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task EachWorkerIsToldWhoItIs_AndSeesTheGoalAndTheBoard()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));
        _client.Script("w2", _client.Call(TeamTools.DoneToolName, new { summary = "nothing" }));

        await RunAsync(CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10)));

        var firstRequests = _client.Requests.GroupBy(r => r.Worker).ToDictionary(g => g.Key, g => g.First().Messages);
        firstRequests.Keys.ShouldBe(["w1", "w2"], ignoreOrder: true);
        firstRequests["w2"][0].Content.ShouldStartWith("You are w2, one of 2 equal workers");
        firstRequests["w2"][1].Content.ShouldContain("Make Class1.Value return 2.");
        firstRequests["w2"][1].Content.ShouldContain("Board now:");
    }

    [Fact]
    public async Task ARunWithNoMerges_DeletesItsTeamBranch()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));

        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10)));

        result.Changes.ShouldBeEmpty();
        result.TeamBranchKept.ShouldBeFalse();
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "branch", "--list", "ideplusplus/ultra/*").ShouldBeEmpty();
    }

    [Fact]
    public async Task AWorkerThatNeverCallsATool_IsStopped_RatherThanCyclingUntilTheDeadline()
    {
        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10)));

        result.EndReason.ShouldBe(UltraTeamEndReason.WorkersStopped);
        _client.Requests.Count.ShouldBe(3);
    }

    [Fact]
    public async Task WhenTimeRunsOut_WorkersStopBetweenSteps_AndTheRunEndsAsTimeUp()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);

        var started = DateTime.UtcNow;
        var run = CreateRun(workers: 2, budget: TimeSpan.FromSeconds(2));
        var result = await RunAsync(run);

        result.EndReason.ShouldBe(UltraTeamEndReason.TimeUp);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(30));
        result.Workers.ShouldAllBe(w => w.Phase == TeamWorkerPhase.Stopped);
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    [Fact]
    public async Task WhenTimeRunsOut_AndTheUserContinues_TheSameTeamWorksAnotherRound()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        var asks = 0;
        var requestsWhenAsked = new List<int>();
        UltraTeamRun? run = null;
        run = CreateRun(workers: 2, budget: TimeSpan.FromSeconds(2), askToContinue: _ =>
        {
            asks++;
            requestsWhenAsked.Add(_client.Requests.Count);
            // Every worker has stopped between steps before the question is asked.
            run!.Workers.ShouldAllBe(w => w.Phase == TeamWorkerPhase.Stopped);
            run.IsAwaitingContinue.ShouldBeTrue();
            return Task.FromResult(asks == 1);
        });

        var result = await RunAsync(run);

        asks.ShouldBe(2);
        run.Round.ShouldBe(2);
        result.EndReason.ShouldBe(UltraTeamEndReason.TimeUp);
        // The second round did real work: more requests by the second question than the first.
        requestsWhenAsked[1].ShouldBeGreaterThan(requestsWhenAsked[0]);
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
    }

    [Fact]
    public async Task Stopping_WhileTheRunAsksWhetherToContinue_EndsItAsCancelled()
    {
        _client.WhenScriptEmpty = _ => _client.Call(TeamTools.BoardReadToolName);
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        using var stop = new CancellationTokenSource();
        var run = CreateRun(workers: 1, budget: TimeSpan.FromSeconds(1), askToContinue: async ct =>
        {
            await stop.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        });

        var result = await RunAsync(run, stop.Token);

        result.EndReason.ShouldBe(UltraTeamEndReason.Cancelled);
        run.Round.ShouldBe(1);
    }

    [Fact]
    public async Task ARunThatFinishesInTime_NeverAsksToContinue()
    {
        _client.Script("w1", _client.Call(TeamTools.DoneToolName, new { summary = "nothing to do" }));
        var asked = false;

        var result = await RunAsync(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(10), askToContinue: _ =>
        {
            asked = true;
            return Task.FromResult(true);
        }));

        result.EndReason.ShouldBe(UltraTeamEndReason.AllDone);
        asked.ShouldBeFalse();
    }

    [Fact]
    public async Task TheCard_AsksToContinue_AndAnswersWithTheButtonPressed()
    {
        var card = new TeamRunViewModel(CreateRun(workers: 1, budget: TimeSpan.FromMinutes(30)), isLocalProvider: false, discardBranches: null);

        var keepGoing = card.AskToContinueAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        card.IsAwaitingContinue.ShouldBeTrue();
        card.ContinueLabel.ShouldBe("Keep going (+30 min)");
        card.KeepGoingCommand.Execute(null);

        (await keepGoing).ShouldBeTrue();
        card.IsAwaitingContinue.ShouldBeFalse();

        using var stop = new CancellationTokenSource();
        var stopped = card.AskToContinueAsync(TimeSpan.FromMinutes(30), stop.Token);
        await stop.CancelAsync();
        (await stopped).ShouldBeFalse();

        var finish = card.AskToContinueAsync(TimeSpan.FromMinutes(30), CancellationToken.None);
        card.FinishNowCommand.Execute(null);
        (await finish).ShouldBeFalse();
    }

    [Fact]
    public async Task Cancelling_EndsTheRunAsCancelled_AndStillRemovesTheWorktrees()
    {
        // Cancelled once a worker is really running - a fixed delay could land during worktree setup
        // on a slow machine, which is a different path (it throws rather than returning a result).
        // Scheduled once: CancelAfter restarts its timer on every call, and replies come every 50 ms.
        using var cancel = new CancellationTokenSource();
        var scheduled = 0;
        _client.WhenScriptEmpty = _ =>
        {
            if (Interlocked.Exchange(ref scheduled, 1) == 0)
            {
                cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
            }

            return _client.Call(TeamTools.BoardReadToolName);
        };
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        var run = CreateRun(workers: 2, budget: TimeSpan.FromMinutes(10));

        var result = await RunAsync(run, cancel.Token);

        result.EndReason.ShouldBe(UltraTeamEndReason.Cancelled);
        Directory.Exists(Worktree(run, "w1", UltraRoot)).ShouldBeFalse();
        Directory.Exists(Worktree(run, "w2", UltraRoot)).ShouldBeFalse();
    }

    [Theory]
    [InlineData(30, 24, 28)]
    [InlineData(5, 4, 4.5)]
    public void Reminders_ComeAt80Percent_ThenTwoMinutesOrTenPercentBeforeTheEnd(
        double budgetMinutes, double deadlineMinutes, double finalMinutes)
    {
        var (deadline, final) = UltraTeamRun.ReminderSchedule(TimeSpan.FromMinutes(budgetMinutes));

        deadline.ShouldBe(TimeSpan.FromMinutes(deadlineMinutes));
        final.ShouldBe(TimeSpan.FromMinutes(finalMinutes));
    }
}
