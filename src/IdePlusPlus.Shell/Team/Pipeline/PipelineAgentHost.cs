using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>How a pipeline agent's run ended.</summary>
internal enum PipelineAgentEnd
{
    /// <summary>It called its submit tool, and the stage has its artifact.</summary>
    Submitted,

    /// <summary>It stopped without submitting - it answered in prose after the reminders, or it ran out of
    /// steps, time, tokens or room after being asked to wrap up. The stage records a gap.</summary>
    NoSubmission,

    /// <summary>There was nothing left of the stage's share when it was due to start, so it never ran.</summary>
    OutOfBudget,

    /// <summary>A step was still running at the stage's hard limit and was cancelled.</summary>
    HardStopped,

    /// <summary>The provider or a tool failed, and the one retry did not help.</summary>
    Failed,
}

/// <summary>
/// What one agent's run produced, for the stage's record. The artifact itself is not here: it arrived
/// through the stage's own submit tool, and the stage holds it. A run that did not submit says why in
/// <see cref="Gap"/>; the stage writes that into the record where the artifact would have been.
/// </summary>
internal sealed record PipelineAgentOutcome(
    string Name,
    PipelineAgentEnd End,
    string? Gap,
    int Reminders,
    WrapUpReason? WrapUp,
    int Iterations,
    int ToolCalls,
    int PromptTokens,
    int CompletionTokens,
    string FinalText)
{
    public bool Submitted => End == PipelineAgentEnd.Submitted;
}

/// <summary>What the host needs to run one agent through one stage.</summary>
internal sealed class PipelineAgentAssignment
{
    /// <summary>u1, d1, b2, j3. Also what the agent's system prompt calls it.</summary>
    public required string Name { get; init; }

    public required string SystemPrompt { get; init; }

    /// <summary>The task, as the first user message.</summary>
    public required string Opening { get; init; }

    /// <summary>What the model is offered, the submit tool included.</summary>
    public required IReadOnlyList<ToolDefinition> Tools { get; init; }

    /// <summary>Runs one tool call. The stage composes it: its own submit tool first, then the
    /// agent's dispatcher (the read-only tools, or the worktree's). It owns any step counting.</summary>
    public required Func<string, JsonNode?, CancellationToken, Task<(bool IsError, string Text)>> ExecuteTool { get; init; }

    /// <summary>The tool that hands in the result. Named in every reminder.</summary>
    public required string SubmitToolName { get; init; }

    /// <summary>True once the stage holds this agent's artifact. A refused submission - one that failed
    /// validation - leaves it false, so the agent is left to correct and call again.</summary>
    public required Func<bool> IsComplete { get; init; }

    /// <summary>The model for this agent; null for the host's default.</summary>
    public string? Model { get; init; }

    public ChatEffort Effort { get; init; } = ChatEffort.Medium;

    /// <summary>The most model requests the agent gets before it is asked to wrap up.</summary>
    public int MaxIterations { get; init; } = 40;

    /// <summary>Called after every tool call; what it returns is appended to that call's result (see
    /// <see cref="HeadlessAgentLoop"/>) - the way an owner hears a peer's request mid-step.</summary>
    public Func<string?>? DrainNotices { get; init; }

    /// <summary>The status card's record of this agent. The host moves it between working, done and
    /// stopped; it does not count steps, since an agent behind a dispatcher already has them counted.</summary>
    public TeamWorkerState? State { get; init; }
}

internal sealed record PipelineAgentHostOptions
{
    /// <summary>How many times an agent that answered without submitting is reminded before the run
    /// records a gap.</summary>
    public int MaxReminders { get; init; } = 2;

    /// <summary>The steps an agent gets after a reminder.</summary>
    public int ReminderIterations { get; init; } = 4;

    /// <summary>The steps an agent gets after being asked to wrap up.</summary>
    public int WrapUpIterations { get; init; } = 3;

    /// <summary>The conversation is cut off for a wrap-up past this many characters.</summary>
    public int MaxHistoryChars { get; init; } = 300_000;

    /// <summary>How many times a run that failed - a dropped connection, a tool that threw - is
    /// resumed where it stood before it is given up on.</summary>
    public int MaxFailureRetries { get; init; } = 1;
}

/// <summary>
/// Runs one pipeline agent: one <see cref="HeadlessAgentLoop"/> over one in-memory conversation, until
/// the agent has submitted its result - which is the only thing a stage is waiting for.
///
/// An agent that does not submit is helped to, then written off, and the host never throws for it:
/// <list type="bullet">
/// <item>Answered without calling the submit tool: reminded, at most twice.</item>
/// <item>Stage time or tokens used up, the conversation too large, or every step used: asked once to
/// submit what it has, with a few steps to do it in.</item>
/// <item>Still nothing, or a step that outlives the stage's hard limit: a gap in the outcome.</item>
/// <item>A failed request or tool: the run resumes where it stood, once.</item>
/// </list>
/// Only the run's own cancellation (the user stopping it) propagates.
///
/// The host stops an agent the moment it has submitted rather than spending a request on its reply,
/// and does not start an agent at all when its stage has nothing left to give it.
/// </summary>
internal sealed class PipelineAgentHost(IChatClient client, string defaultModel, PipelineAgentHostOptions? options = null)
{
    private readonly PipelineAgentHostOptions _options = options ?? new PipelineAgentHostOptions();

    /// <summary>Runs every assignment at once and returns their outcomes in the same order.</summary>
    public async Task<IReadOnlyList<PipelineAgentOutcome>> RunAllAsync(
        IReadOnlyList<PipelineAgentAssignment> agents, StageClock clock, CancellationToken cancellationToken)
    {
        var outcomes = await Task.WhenAll(agents.Select(agent => RunAsync(agent, clock, cancellationToken)));
        return outcomes;
    }

    public async Task<PipelineAgentOutcome> RunAsync(
        PipelineAgentAssignment agent, StageClock clock, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var totals = new Totals();
        if (clock.IsOver || clock.HardStop.IsCancellationRequested)
        {
            if (agent.State is { } idle)
            {
                idle.Phase = TeamWorkerPhase.Stopped;
            }

            return Finish(agent, totals, PipelineAgentEnd.OutOfBudget, "no time or tokens were left for it when its turn came");
        }

        if (agent.State is { } state)
        {
            state.Phase = TeamWorkerPhase.Working;
        }

        PipelineAgentOutcome? outcome = null;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clock.HardStop);
            outcome = await ConverseAsync(agent, clock, totals, cancellationToken, linked.Token);
            return outcome;
        }
        finally
        {
            if (agent.State is { } finished)
            {
                // A cancelled run has no outcome and is stopped, like any agent that did not submit.
                finished.Phase = outcome?.Submitted == true ? TeamWorkerPhase.Done : TeamWorkerPhase.Stopped;
            }
        }
    }

    private async Task<PipelineAgentOutcome> ConverseAsync(
        PipelineAgentAssignment agent, StageClock clock, Totals totals, CancellationToken runToken, CancellationToken token)
    {
        var loop = new HeadlessAgentLoop(
            client, agent.Model ?? defaultModel, agent.Effort, agent.Tools, agent.ExecuteTool, agent.DrainNotices);
        var history = new List<ChatMessage>
        {
            new(ChatRole.System, agent.SystemPrompt),
            new(ChatRole.User, agent.Opening),
        };

        // Stop between steps - never half way through one - when the agent has submitted, or its stage
        // has nothing left to give it, or its conversation is too large to go on with.
        bool Full() => agent.IsComplete() || clock.IsOver || HistoryChars(history) > _options.MaxHistoryChars;

        var iterations = agent.MaxIterations;
        var wrapUp = (WrapUpReason?)null;
        var reminders = 0;
        var failures = 0;

        while (true)
        {
            HeadlessRunResult result;
            try
            {
                // Once asked to wrap up, only a submission stops it: the budget is already spent and
                // the hard limit is what bounds the rest.
                result = await loop.RunAsync(
                    history, iterations, token, stopBeforeNextIteration: wrapUp is null ? Full : agent.IsComplete);
            }
            catch (OperationCanceledException) when (runToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException) when (clock.HardStop.IsCancellationRequested)
            {
                return Finish(agent, totals, PipelineAgentEnd.HardStopped, "a step was still running at the stage's hard limit", reminders, wrapUp);
            }
            catch (Exception ex)
            {
                if (failures < _options.MaxFailureRetries && !clock.IsOver)
                {
                    failures++;
                    CloseOpenToolCalls(history);
                    continue;
                }

                return Finish(agent, totals, PipelineAgentEnd.Failed, $"it failed: {ex.Message}", reminders, wrapUp);
            }

            totals.Add(result);
            if (agent.IsComplete())
            {
                return Finish(agent, totals, PipelineAgentEnd.Submitted, null, reminders, wrapUp, result.FinalText);
            }

            var pressure = result.StoppedEarly
                ? clock.OverBecause switch
                {
                    StageOverReason.Time => WrapUpReason.Time,
                    StageOverReason.Tokens => WrapUpReason.Tokens,
                    _ => WrapUpReason.Context,
                }
                : result.HitIterationCap ? WrapUpReason.Steps : (WrapUpReason?)null;

            if (pressure is { } why)
            {
                if (wrapUp is not null)
                {
                    return Finish(
                        agent, totals, PipelineAgentEnd.NoSubmission, $"it did not submit after being asked to wrap up ({Describe(why)})",
                        reminders, wrapUp, result.FinalText);
                }

                wrapUp = why;
                iterations = _options.WrapUpIterations;
                history.Add(new ChatMessage(ChatRole.User, PipelinePrompts.WrapUp(agent.SubmitToolName, why)));
                continue;
            }

            // It answered in prose and stopped.
            if (reminders >= _options.MaxReminders)
            {
                return Finish(
                    agent, totals, PipelineAgentEnd.NoSubmission,
                    $"it answered without calling {agent.SubmitToolName}, after {reminders} reminder(s)", reminders, wrapUp, result.FinalText);
            }

            reminders++;
            iterations = _options.ReminderIterations;
            history.Add(new ChatMessage(
                ChatRole.User, PipelinePrompts.MissingSubmission(agent.SubmitToolName, reminders, _options.MaxReminders)));
        }
    }

    private static PipelineAgentOutcome Finish(
        PipelineAgentAssignment agent,
        Totals totals,
        PipelineAgentEnd end,
        string? gap,
        int reminders = 0,
        WrapUpReason? wrapUp = null,
        string? finalText = null) =>
        new(
            agent.Name, end, gap is null ? null : $"{agent.Name}: {gap}", reminders, wrapUp,
            totals.Iterations, totals.ToolCalls, totals.PromptTokens, totals.CompletionTokens, finalText ?? "");

    private static string Describe(WrapUpReason reason) => reason switch
    {
        WrapUpReason.Time => "its stage ran out of time",
        WrapUpReason.Tokens => "its stage ran out of tokens",
        WrapUpReason.Context => "its conversation grew too large",
        _ => "it used every step it was given",
    };

    /// <summary>
    /// After a failure part way through a turn the conversation can end on an assistant message whose
    /// tool calls were not all answered, which a provider refuses outright. Each unanswered call gets a
    /// result that says so - the step may have run in part - so the run can be resumed.
    /// </summary>
    internal static void CloseOpenToolCalls(List<ChatMessage> history)
    {
        var last = history.FindLastIndex(m => m.Role == ChatRole.Assistant && m.ToolCalls is { Count: > 0 });
        if (last < 0)
        {
            return;
        }

        var answered = history.Skip(last + 1)
            .Where(m => m.Role == ChatRole.Tool && m.ToolCallId is not null)
            .Select(m => m.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var call in history[last].ToolCalls!.Where(c => !answered.Contains(c.Id)))
        {
            history.Add(new ChatMessage(
                ChatRole.Tool,
                "Error: this step was interrupted by a failure and may not have completed. Check the state before relying on it.",
                ToolCallId: call.Id));
        }
    }

    private static int HistoryChars(List<ChatMessage> history) =>
        history.Sum(m => m.Content.Length + (m.ToolCalls?.Sum(c => c.ArgumentsJson.Length) ?? 0));

    private sealed class Totals
    {
        public int Iterations { get; private set; }

        public int ToolCalls { get; private set; }

        public int PromptTokens { get; private set; }

        public int CompletionTokens { get; private set; }

        public void Add(HeadlessRunResult result)
        {
            Iterations += result.Iterations;
            ToolCalls += result.ToolCallCount;
            PromptTokens += result.PromptTokens;
            CompletionTokens += result.CompletionTokens;
        }
    }
}
