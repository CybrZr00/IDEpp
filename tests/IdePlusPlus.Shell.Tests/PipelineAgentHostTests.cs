using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.Shell.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// One pipeline agent's run: it ends when the agent has submitted, is helped to if it has not, and is
/// written off as a gap - never thrown for - when nothing helps. The agents here are surveyors, u1 and
/// u2, whose replies are scripted; the stage's clock is a <see cref="ManualClock"/>, so running a stage
/// out of time is one call to Advance.
/// </summary>
public sealed class PipelineAgentHostTests
{
    private const string Submit = "survey_submit";
    private static readonly JsonNode Schema = JsonNode.Parse("""{"type":"object","properties":{}}""")!;

    private readonly ScriptedTeamChatClient _client = new();
    private readonly ManualClock _time = new();
    private long _tokens;

    /// <summary>Twenty-six minutes, so the first stage's share is two.</summary>
    private PipelineBudget Budget(long maxTokens = 0) => new(
        _time, TimeSpan.FromMinutes(26), maxTokens, maxRepairLoops: 3, TimeSpan.FromMinutes(2), () => Interlocked.Read(ref _tokens));

    private StageClock Stage(long maxTokens = 0) => Budget(maxTokens).BeginStage(UltraStage.Understand);

    private PipelineAgentHost Host(PipelineAgentHostOptions? options = null, IChatClient? client = null) =>
        new(client ?? _client, "chat-model", options);

    private static ChatStreamEvent[] Text(string text) => [new TextDelta(text)];

    /// <summary>What a stage keeps for an agent: whether it has submitted, and what it was called for.</summary>
    private sealed class Rig
    {
        public bool Submitted { get; set; }

        public int RefusedSubmissions { get; set; }

        public List<string> Calls { get; } = [];

        public Func<string, JsonNode?, CancellationToken, Task<(bool IsError, string Text)>>? OnTool { get; set; }
    }

    private static PipelineAgentAssignment Agent(
        string name,
        Rig rig,
        int maxIterations = 40,
        TeamWorkerState? state = null,
        string? model = null,
        ChatEffort effort = ChatEffort.Medium,
        Func<string?>? drainNotices = null) => new()
    {
        Name = name,
        SystemPrompt = $"You are {name}, a surveyor. Report what you find.",
        Opening = "Survey the Lib folder.",
        Tools = [new ToolDefinition("read_file", "Read a file", Schema), new ToolDefinition(Submit, "Hand in the survey", Schema)],
        SubmitToolName = Submit,
        IsComplete = () => rig.Submitted,
        MaxIterations = maxIterations,
        State = state,
        Model = model,
        Effort = effort,
        DrainNotices = drainNotices,
        ExecuteTool = async (tool, arguments, ct) =>
        {
            rig.Calls.Add(tool);
            if (tool == Submit)
            {
                if (rig.RefusedSubmissions > 0)
                {
                    rig.RefusedSubmissions--;
                    return (true, "Not recorded: the survey has no findings. Fix that and call survey_submit again.");
                }

                rig.Submitted = true;
                return (false, "Recorded.");
            }

            return rig.OnTool is null ? (false, "file contents") : await rig.OnTool(tool, arguments, ct);
        },
    };

    // ---- The ordinary run ----

    [Fact]
    public async Task AnAgentThatSubmits_Stops_WithoutSpendingARequestOnItsReply()
    {
        var rig = new Rig();
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.End.ShouldBe(PipelineAgentEnd.Submitted);
        outcome.Submitted.ShouldBeTrue();
        outcome.Gap.ShouldBeNull();
        outcome.Reminders.ShouldBe(0);
        outcome.WrapUp.ShouldBeNull();
        rig.Calls.ShouldBe(["read_file", Submit]);
        _client.Requests.Count.ShouldBe(2);
        (outcome.Iterations, outcome.ToolCalls).ShouldBe((2, 2));
        (outcome.PromptTokens, outcome.CompletionTokens).ShouldBe((200, 20));
    }

    [Fact]
    public async Task TheAgent_IsGivenItsPromptAndTask_AndItsOwnModelAndEffort()
    {
        var rig = new Rig();
        _client.Script("d1", _client.Call(Submit));
        using var stage = Stage();

        await Host().RunAsync(Agent("d1", rig, model: "design-model", effort: ChatEffort.High), stage, CancellationToken.None);

        // The test client finds the agent by its prompt's opening words, "You are d1, ".
        var request = _client.Requests.Single();
        request.Worker.ShouldBe("d1");
        request.Messages.Select(m => (m.Role, m.Content)).ShouldBe(
        [
            (ChatRole.System, "You are d1, a surveyor. Report what you find."),
            (ChatRole.User, "Survey the Lib folder."),
        ]);
        _client.Models.Single().ShouldBe(("d1", "design-model", ChatEffort.High));
    }

    /// <summary>One host runs every pipeline agent - the lead included - so this is the one place the user's
    /// conventions have to be added for all of them. Without any, the test above shows the prompt untouched.</summary>
    [Fact]
    public async Task TheConventions_AreAddedAtTheEndOfTheAgentsPrompt_AndNothingElseChanges()
    {
        var rig = new Rig();
        _client.Script("u1", _client.Call(Submit));
        using var stage = Stage();

        await Host(new PipelineAgentHostOptions { Conventions = "Nullable is on.\r\nNo .Result." })
            .RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        var request = _client.Requests.Single();
        request.Worker.ShouldBe("u1");
        request.Messages[0].Role.ShouldBe(ChatRole.System);
        request.Messages[0].Content.ShouldStartWith("You are u1, a surveyor. Report what you find.\n\nProject conventions");
        request.Messages[0].Content.ShouldEndWith("\n\nNullable is on.\nNo .Result.");
        (request.Messages[1].Role, request.Messages[1].Content).ShouldBe((ChatRole.User, "Survey the Lib folder."));
    }

    [Fact]
    public async Task WithNoModelOfItsOwn_TheAgent_UsesTheHostsDefault()
    {
        var rig = new Rig();
        _client.Script("u1", _client.Call(Submit));
        using var stage = Stage();

        await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        _client.Models.Single().Model.ShouldBe("chat-model");
    }

    [Fact]
    public async Task ASubmissionTheStageRefuses_IsNotAMissingOne_TheAgentIsLeftToFixAndCallAgain()
    {
        var rig = new Rig { RefusedSubmissions = 1 };
        _client.Script("u1", _client.Call(Submit), _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.Reminders.ShouldBe(0);
        _client.Requests.Count.ShouldBe(2);
        _client.Requests.Last().Messages.Last().Content.ShouldStartWith("Not recorded");
    }

    [Fact]
    public async Task WhatTheAgentsDispatcherHasToSay_IsAppendedToTheToolResult()
    {
        var rig = new Rig();
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage();

        await Host().RunAsync(Agent("u1", rig, drainNotices: () => "b2 asks: may I change Lib/A.cs?"), stage, CancellationToken.None);

        _client.Requests.Last().Messages.Last().Content.ShouldBe("file contents\n\nb2 asks: may I change Lib/A.cs?");
    }

    // ---- Answering without submitting ----

    [Fact]
    public async Task AnAgentThatAnswersInProse_IsRemindedTwice_ThenRecordedAsAGap()
    {
        var rig = new Rig();
        _client.Script("u1", Text("I looked around."), Text("Still thinking."), Text("That is all."));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.End.ShouldBe(PipelineAgentEnd.NoSubmission);
        outcome.Submitted.ShouldBeFalse();
        outcome.Reminders.ShouldBe(2);
        outcome.Gap.ShouldBe("u1: it answered without calling survey_submit, after 2 reminder(s)");
        outcome.FinalText.ShouldBe("That is all.");
        _client.Requests.Count.ShouldBe(3);
        _client.Requests.ElementAt(1).Messages.Last().Content.ShouldBe(PipelinePrompts.MissingSubmission(Submit, 1, 2));
        _client.Requests.ElementAt(2).Messages.Last().Content.ShouldBe(PipelinePrompts.MissingSubmission(Submit, 2, 2));
    }

    [Fact]
    public async Task AReminderThatWorks_LetsTheAgentSubmit()
    {
        var rig = new Rig();
        _client.Script("u1", Text("I am done."), _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.Reminders.ShouldBe(1);
        _client.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task TheNumberOfReminders_IsAnOption()
    {
        var rig = new Rig();
        using var stage = Stage();

        var outcome = await Host(new PipelineAgentHostOptions { MaxReminders = 0 })
            .RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.End.ShouldBe(PipelineAgentEnd.NoSubmission);
        outcome.Reminders.ShouldBe(0);
        _client.Requests.Count.ShouldBe(1);
    }

    // ---- Running out of room ----

    [Fact]
    public async Task AnAgentThatUsesEveryStep_IsAskedOnceToWrapUp_AndCanSubmitThen()
    {
        var rig = new Rig();
        _client.Script("u1", _client.Call("read_file"), _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig, maxIterations: 2), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.WrapUp.ShouldBe(WrapUpReason.Steps);
        outcome.Reminders.ShouldBe(0);
        _client.Requests.Count.ShouldBe(3);
        _client.Requests.ElementAt(2).Messages.Last().Content.ShouldBe(PipelinePrompts.WrapUp(Submit, WrapUpReason.Steps));
    }

    [Fact]
    public async Task AnAgentThatStillDoesNotSubmit_AfterItsWrapUp_IsAGap_WithAFewMoreStepsAtMost()
    {
        var rig = new Rig();
        _client.WhenScriptEmpty = _ => _client.Call("read_file");
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig, maxIterations: 2), stage, CancellationToken.None);

        outcome.End.ShouldBe(PipelineAgentEnd.NoSubmission);
        outcome.WrapUp.ShouldBe(WrapUpReason.Steps);
        outcome.Gap.ShouldNotBeNull().ShouldContain("asked to wrap up");
        outcome.Gap.ShouldContain("every step");
        // Its two steps, then the three it was given to wrap up in - and no further.
        _client.Requests.Count.ShouldBe(5);
    }

    [Fact]
    public async Task WhenTheStagesTokensRunOut_TheAgentIsToldSo_AndAskedToSubmit()
    {
        var rig = new Rig
        {
            OnTool = (_, _, _) =>
            {
                Interlocked.Add(ref _tokens, 2_500); // the first stage's share of 26,000 is 2,000
                return Task.FromResult((false, "file contents"));
            },
        };
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage(maxTokens: 26_000);

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.WrapUp.ShouldBe(WrapUpReason.Tokens);
        _client.Requests.Last().Messages.Last().Content.ShouldBe(PipelinePrompts.WrapUp(Submit, WrapUpReason.Tokens));
    }

    [Fact]
    public async Task WhenTheStagesTimeRunsOut_TheAgentIsToldSo_AndAskedToSubmit()
    {
        var rig = new Rig
        {
            OnTool = (_, _, _) =>
            {
                _time.Advance(TimeSpan.FromMinutes(3)); // past the share of two, short of the hard limit
                return Task.FromResult((false, "file contents"));
            },
        };
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.WrapUp.ShouldBe(WrapUpReason.Time);
        _client.Requests.Last().Messages.Last().Content.ShouldBe(PipelinePrompts.WrapUp(Submit, WrapUpReason.Time));
    }

    [Fact]
    public async Task AConversationThatGrowsTooLarge_IsAskedToWrapUp()
    {
        var rig = new Rig { OnTool = (_, _, _) => Task.FromResult((false, new string('x', 2_000))) };
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host(new PipelineAgentHostOptions { MaxHistoryChars = 500 })
            .RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.WrapUp.ShouldBe(WrapUpReason.Context);
        _client.Requests.Last().Messages.Last().Content.ShouldBe(PipelinePrompts.WrapUp(Submit, WrapUpReason.Context));
    }

    [Fact]
    public async Task AnAgentWhoseStageHasNothingLeft_NeverRuns_AndCostsNoRequest()
    {
        var rig = new Rig();
        var state = new TeamWorkerState("u1");
        using var stage = Stage();
        _time.Advance(TimeSpan.FromMinutes(3));

        var outcome = await Host().RunAsync(Agent("u1", rig, state: state), stage, CancellationToken.None);

        outcome.End.ShouldBe(PipelineAgentEnd.OutOfBudget);
        outcome.Gap.ShouldNotBeNull().ShouldStartWith("u1:");
        _client.Requests.ShouldBeEmpty();
        rig.Calls.ShouldBeEmpty();
        state.Phase.ShouldBe(TeamWorkerPhase.Stopped);
    }

    [Fact]
    public async Task AStageWithNoTimeAtAll_IsBornStopped_AndItsAgentsDoNotRun()
    {
        var spent = new PipelineBudget(_time, TimeSpan.Zero, 0, 3, TimeSpan.Zero, () => 0);
        using var stage = spent.BeginStage(UltraStage.Understand);

        var outcome = await Host().RunAsync(Agent("u1", new Rig()), stage, CancellationToken.None);

        stage.HardStop.IsCancellationRequested.ShouldBeTrue();
        outcome.End.ShouldBe(PipelineAgentEnd.OutOfBudget);
        _client.Requests.ShouldBeEmpty();
    }

    // ---- The hard limit ----

    [Fact]
    public async Task AStep_StillRunningAtTheHardLimit_IsCancelled_AndTheAgentIsRecordedAsStopped()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rig = new Rig
        {
            OnTool = async (_, _, ct) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, ct); // a step that never ends, until it is cancelled
                return (false, "unreachable");
            },
        };
        _client.Script("u1", _client.Call("read_file"));
        var state = new TeamWorkerState("u1");
        using var stage = Stage();

        var run = Host().RunAsync(Agent("u1", rig, state: state), stage, CancellationToken.None);
        await started.Task;
        state.Phase.ShouldBe(TeamWorkerPhase.Working);
        _time.Advance(TimeSpan.FromMinutes(5)); // the share of two plus the grace of two
        var outcome = await run;

        outcome.End.ShouldBe(PipelineAgentEnd.HardStopped);
        outcome.Gap.ShouldNotBeNull().ShouldContain("hard limit");
        state.Phase.ShouldBe(TeamWorkerPhase.Stopped);
    }

    [Fact]
    public void TheHardStop_FiresAtTheShareAndTheGrace_NotBefore()
    {
        using var stage = Stage();

        _time.Advance(TimeSpan.FromMinutes(3));
        stage.IsOver.ShouldBeTrue();
        stage.HardStop.IsCancellationRequested.ShouldBeFalse();

        _time.Advance(TimeSpan.FromMinutes(1));
        stage.HardStop.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task TheRunsOwnCancellation_Propagates_AndLeavesTheAgentStopped()
    {
        using var cancel = new CancellationTokenSource();
        var rig = new Rig
        {
            OnTool = (_, _, ct) =>
            {
                cancel.Cancel();
                ct.ThrowIfCancellationRequested();
                return Task.FromResult((false, "unreachable"));
            },
        };
        _client.Script("u1", _client.Call("read_file"));
        var state = new TeamWorkerState("u1");
        using var stage = Stage();

        await Should.ThrowAsync<OperationCanceledException>(
            () => Host().RunAsync(Agent("u1", rig, state: state), stage, cancel.Token));

        state.Phase.ShouldBe(TeamWorkerPhase.Stopped);
    }

    // ---- Failures ----

    [Fact]
    public async Task AFailedRequest_IsRetriedOnce_FromWhereTheAgentStood()
    {
        var rig = new Rig();
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        using var stage = Stage();

        // The second request fails before the model sees it; the third is the same request again.
        var outcome = await Host(client: new FlakyChatClient(_client, failOnCalls: 2))
            .RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        outcome.Reminders.ShouldBe(0);
        _client.Requests.Count.ShouldBe(2);
        _client.Requests.Last().Messages.Select(m => m.Role).ShouldBe(
            [ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool]);
    }

    [Fact]
    public async Task AFailureThatRepeats_EndsTheRunAsFailed_WithoutThrowing()
    {
        var rig = new Rig();
        using var stage = Stage();

        var outcome = await Host(client: new FlakyChatClient(_client, failOnCalls: [1, 2]))
            .RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.End.ShouldBe(PipelineAgentEnd.Failed);
        outcome.Gap.ShouldNotBeNull().ShouldContain("connection reset");
        outcome.Gap.ShouldStartWith("u1:");
        _client.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AToolThatThrowsMidTurn_LeavesNoToolCallUnanswered_WhenTheRunResumes()
    {
        var failures = 0;
        var rig = new Rig
        {
            OnTool = (_, _, _) => ++failures == 1
                ? throw new InvalidOperationException("disk gone")
                : Task.FromResult((false, "file contents")),
        };
        _client.Script(
            "u1",
            [new ToolCallDelta(0, "c1", "read_file", "{}"), new ToolCallDelta(1, "c2", "read_file", "{}")],
            _client.Call(Submit));
        using var stage = Stage();

        var outcome = await Host().RunAsync(Agent("u1", rig), stage, CancellationToken.None);

        outcome.Submitted.ShouldBeTrue();
        // The provider refuses a conversation with a tool call that has no result, so each of the turn's
        // two calls has one - and the one that was cut off says it may not have completed.
        var resumed = _client.Requests.Last().Messages.Where(m => m.Role == ChatRole.Tool).ToList();
        resumed.Select(m => m.ToolCallId).ShouldBe(["c1", "c2"]);
        resumed.ShouldAllBe(m => m.Content.StartsWith("Error: this step was interrupted"));
    }

    [Fact]
    public void ClosingOpenToolCalls_AnswersOnlyTheCallsThatHaveNoResult()
    {
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, "s"),
            new(ChatRole.User, "u"),
            new(ChatRole.Assistant, "", ToolCalls: [new ChatToolCall("c1", "read_file", "{}"), new ChatToolCall("c2", "read_file", "{}"), new ChatToolCall("c3", "read_file", "{}")]),
            new(ChatRole.Tool, "done", ToolCallId: "c1"),
        };

        PipelineAgentHost.CloseOpenToolCalls(history);

        history.Where(m => m.Role == ChatRole.Tool).Select(m => m.ToolCallId).ShouldBe(["c1", "c2", "c3"]);
        history[3].Content.ShouldBe("done");

        // Done again, there is nothing more to add.
        PipelineAgentHost.CloseOpenToolCalls(history);
        history.Count.ShouldBe(6);
    }

    [Fact]
    public void ClosingOpenToolCalls_LeavesAConversationWithNoneAlone()
    {
        var history = new List<ChatMessage> { new(ChatRole.System, "s"), new(ChatRole.User, "u"), new(ChatRole.Assistant, "hello") };

        PipelineAgentHost.CloseOpenToolCalls(history);

        history.Count.ShouldBe(3);
    }

    // ---- The status card and several agents ----

    [Fact]
    public async Task TheAgentsCard_IsWorkingWhileItRuns_ThenDoneWhenItSubmits()
    {
        TeamWorkerPhase? during = null;
        var state = new TeamWorkerState("u1");
        var rig = new Rig
        {
            OnTool = (_, _, _) =>
            {
                during = state.Phase;
                return Task.FromResult((false, "file contents"));
            },
        };
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        state.Phase.ShouldBe(TeamWorkerPhase.Waiting);
        using var stage = Stage();

        await Host().RunAsync(Agent("u1", rig, state: state), stage, CancellationToken.None);

        during.ShouldBe(TeamWorkerPhase.Working);
        state.Phase.ShouldBe(TeamWorkerPhase.Done);
    }

    [Fact]
    public async Task TheAgentsCard_IsStopped_WhenItRecordsAGap()
    {
        var state = new TeamWorkerState("u1");
        using var stage = Stage();

        await Host(new PipelineAgentHostOptions { MaxReminders = 0 }).RunAsync(Agent("u1", new Rig(), state: state), stage, CancellationToken.None);

        state.Phase.ShouldBe(TeamWorkerPhase.Stopped);
    }

    [Fact]
    public async Task SeveralAgents_RunAtOnce_AndTheirOutcomesComeBackInTheOrderTheyWereGiven()
    {
        _client.DelayPerReply = TimeSpan.FromMilliseconds(50);
        var first = new Rig();
        var second = new Rig();
        _client.Script("u1", _client.Call("read_file"), _client.Call(Submit));
        _client.Script("u2", _client.Call(Submit));
        using var stage = Stage();

        var outcomes = await Host().RunAllAsync([Agent("u1", first), Agent("u2", second)], stage, CancellationToken.None);

        outcomes.Select(o => (o.Name, o.Submitted)).ShouldBe([("u1", true), ("u2", true)]);
        _client.MaxInFlight.ShouldBeGreaterThanOrEqualTo(2);
        first.Calls.ShouldBe(["read_file", Submit]);
        second.Calls.ShouldBe([Submit]);
    }

    [Fact]
    public async Task OneAgentsGap_DoesNotStopItsPeers()
    {
        _client.Script("u1", Text("nothing to say"));
        _client.Script("u2", _client.Call(Submit));
        using var stage = Stage();

        var outcomes = await Host(new PipelineAgentHostOptions { MaxReminders = 0 })
            .RunAllAsync([Agent("u1", new Rig()), Agent("u2", new Rig())], stage, CancellationToken.None);

        outcomes.Select(o => (o.Name, o.End)).ShouldBe(
            [("u1", PipelineAgentEnd.NoSubmission), ("u2", PipelineAgentEnd.Submitted)]);
    }

    /// <summary>Fails the requests numbered in <paramref name="failOnCalls"/> before they reach the model,
    /// the way a dropped connection does, and passes the rest through.</summary>
    private sealed class FlakyChatClient(IChatClient inner, params int[] failOnCalls) : IChatClient
    {
        private int _calls;

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            IReadOnlyList<ChatMessage> messages,
            string model,
            IReadOnlyList<ToolDefinition>? tools = null,
            ChatEffort effort = ChatEffort.None,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (failOnCalls.Contains(Interlocked.Increment(ref _calls)))
            {
                throw new HttpRequestException("connection reset");
            }

            await foreach (var streamEvent in inner.StreamChatAsync(messages, model, tools, effort, cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }
}
