using System.IO;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>
/// An Ultra run as a pipeline of stages instead of a team of workers: the orchestrator decides who is on
/// the roster, surveyors read the code, one designer plans, owners build, and judges and owners then
/// loop - judge, repair - until the judges pass or the loop's cap or the run's budget is reached.
///
/// This is a deterministic stage machine. It is the runner, not an agent: it decides nothing a rule can
/// decide, starts each stage's agents, collects what they submit, writes every record of the run to
/// <c>&lt;run&gt;\_phases</c>, and keeps the time and tokens each stage may spend. The orchestrator
/// (<see cref="PipelineConductor"/>) is called only where judgement is needed. The agents never talk to
/// each other except through the runner's own tools; what one stage hands the next is an artifact.
///
/// Stages so far: Setup (snapshot and branch), Roster (the orchestrator), Understand (surveyors) and
/// Finalize (records and result). Design, Build and the loop are added stage by stage; until then a run
/// that gets through the stages it has ends <see cref="UltraTeamEndReason.AllDone"/>.
///
/// Like the team run it always cleans up - worktrees, links, branches that hold nothing - and a user's
/// cancellation ends the run with <see cref="UltraTeamEndReason.Cancelled"/> and a result, not an exception.
/// </summary>
public sealed class UltraPipelineRun : IUltraRun
{
    /// <summary>The most worktrees counted when the disk is checked before anything exists: the read-only
    /// checkout, the designer's and the gate's. Owners and test-writing judges are counted when they are
    /// known.</summary>
    private const int InitialWorktrees = 3;

    private const int SurveyorIterations = 30;

    private readonly IChatClient _workerClient;
    private readonly IChatClient _leadClient;
    private readonly string _model;
    private readonly UltraTeamOptions _options;
    private readonly ReadOnlyToolDispatcher _readOnlyTools;
    private readonly TeamGitWorkspace _teamGit;
    private readonly TimeProvider _time;
    private readonly string _ultraRootDirectory;
    private readonly PipelineBudget _budget;
    private readonly PipelineAgentHost _workerHost;
    private readonly PipelineAgentHost _leadHost;

    private readonly object _lock = new();
    private readonly List<TeamWorkerState> _agents = [];
    private readonly List<StageRecord> _stages = [];
    private readonly List<string> _gaps = [];

    private int _promptTokens;
    private int _completionTokens;
    private int _cachedPromptTokens;
    private volatile string? _setupStatus;
    private volatile string? _stageText;
    private RunWorkspace? _workspace;
    private PipelineArtifactStore? _phases;
    private RosterPlan _roster = new([], [], null);
    private string _goal = "";

    /// <param name="ultraRootDirectory">Where run folders go - outside any repository, so worktrees never
    /// show up in the user's status, file list or index.</param>
    public UltraPipelineRun(
        IChatClient client,
        string model,
        UltraTeamOptions options,
        ReadOnlyToolDispatcher readOnlyTools,
        TeamGitWorkspace teamGit,
        TimeProvider time,
        string ultraRootDirectory)
    {
        // One throttle per provider: each has its own rate limits, and the run's usage counts them all.
        var wrapped = new Dictionary<IChatClient, IChatClient>(ReferenceEqualityComparer.Instance);
        IChatClient Wrap(IChatClient inner) =>
            wrapped.TryGetValue(inner, out var existing)
                ? existing
                : wrapped[inner] = new UsageCountingChatClient(new ThrottlingChatClient(inner, options, time), OnUsage);
        _workerClient = Wrap(options.WorkerClient ?? client);
        _leadClient = Wrap(options.OrchestratorClient ?? client);
        _model = model;
        _options = options;
        _readOnlyTools = readOnlyTools;
        _teamGit = teamGit;
        _time = time;
        _ultraRootDirectory = ultraRootDirectory;

        RunId = $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..4]}";
        Board = new TeamBoard(time, options.ClaimLifetime);
        _budget = new PipelineBudget(
            time,
            options.TimeBudget,
            options.MaxTokens,
            Math.Max(0, options.PipelineMaxLoops),
            options.HardStopGrace,
            () => (long)PromptTokens + CompletionTokens);
        _workerHost = new PipelineAgentHost(_workerClient, options.WorkerModel ?? model);
        _leadHost = new PipelineAgentHost(_leadClient, options.OrchestratorModel ?? model);
    }

    // ---- Status, as the card reads it ----

    public string RunId { get; }

    /// <summary>Nothing is posted here: the pipeline's agents hand in artifacts, and the runner keeps the record.</summary>
    public TeamBoard Board { get; }

    /// <summary>The run's folder, where the worktrees and the records are.</summary>
    internal string RunDirectory => Path.Combine(_ultraRootDirectory, RunId);

    /// <summary>The records the runner writes - the roster, the surveys, each stage - once the run has begun.</summary>
    internal string PhasesDirectory => Path.Combine(RunDirectory, RunWorkspace.PhasesFolderName);

    /// <summary>Every agent that has been given a turn so far, in the order they started.</summary>
    public IReadOnlyList<TeamWorkerState> Workers
    {
        get
        {
            lock (_lock)
            {
                return [.. _agents];
            }
        }
    }

    public int MaxWorkers => Workers.Count;

    public bool IsAutoScaling => false;

    public TimeSpan TimeBudget => _options.TimeBudget;

    /// <summary>Time since the first stage began; the setup before it is not charged to the run.</summary>
    public TimeSpan Elapsed => _budget.Elapsed;

    public TimeSpan TimeLeft => _budget.TimeLeft;

    public int Round => 1;

    public string? SetupStatus => _setupStatus;

    public bool IsCheckingBuild => false;

    public bool IsAwaitingContinue => false;

    public string? PauseExplanation => null;

    public bool IsWrappingUp => false;

    public string? StageText => _stageText;

    public long MaxTokens => _options.MaxTokens;

    public int PromptTokens => Volatile.Read(ref _promptTokens);

    public int CompletionTokens => Volatile.Read(ref _completionTokens);

    public int CachedPromptTokens => Volatile.Read(ref _cachedPromptTokens);

    public string? StartedFrom => _workspace?.StartedFrom;

    public int CarriedBoardEntries => 0;

    public event Action? Changed;

    // ---- The run ----

    public async Task<UltraTeamResult> RunAsync(
        string goal,
        string repositoryRoot,
        string solutionPath,
        IReadOnlyList<string> testProjectPaths,
        CancellationToken cancellationToken,
        string? startFrom = null,
        IReadOnlyList<string>? backlog = null)
    {
        _goal = goal;
        var workspace = _workspace = new RunWorkspace(
            _teamGit, _ultraRootDirectory, RunId, repositoryRoot, solutionPath, _options.FreeDiskSpace, SetSetup);

        // Refused here - before there is anything to clean up - if the starting branch conflicts with the
        // working tree or the disk is short.
        await workspace.OpenAsync(startFrom, InitialWorktrees, cancellationToken);
        _phases = new PipelineArtifactStore(workspace.PhasesDirectory);

        IReadOnlyList<string> keptBranches = [];
        var endReason = UltraTeamEndReason.Cancelled;
        try
        {
            try
            {
                SetSetup("Creating workspace - checking out the read-only copy the agents read");
                var readOnly = await workspace.AddDetachedWorktreeAsync(
                    RunWorkspace.ReadOnlyFolderName, workspace.Start, linkDependencies: true, cancellationToken);
                SetSetup(null);

                endReason = await RunStagesAsync(goal, ApprovedPlan(backlog), workspace, readOnly, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                endReason = UltraTeamEndReason.Cancelled;
            }
            finally
            {
                // Not the run's token: a cancelled run is exactly the one whose worktrees most need removing.
                keptBranches = await workspace.CloseAsync();
                foreach (var agent in Workers.Where(w => w.Phase is TeamWorkerPhase.Working or TeamWorkerPhase.Waiting))
                {
                    agent.Phase = TeamWorkerPhase.Stopped;
                }

                SetStage(null);
                SetSetup(null);
            }
        }
        catch (UltraTeamDiskSpaceException)
        {
            // Nothing was done, so nothing is kept: the team branch only marks where it would have started.
            await workspace.DiscardTeamBranchAsync();
            throw;
        }

        await WriteSummaryAsync(endReason);
        var outcome = await workspace.FinishAsync(CancellationToken.None);
        return new UltraTeamResult(
            RunId, repositoryRoot, workspace.Snapshot, workspace.TeamBranch, outcome.TeamBranchKept, outcome.Changes, endReason,
            Workers, Board.Snapshot(), PromptTokens, CompletionTokens, keptBranches);
    }

    /// <summary>The steps the user approved in Plan Mode as one numbered list, or null if there were none.</summary>
    private static string? ApprovedPlan(IReadOnlyList<string>? backlog) =>
        backlog is { Count: > 0 }
            ? string.Join("\n", backlog.Select((step, index) => $"{index + 1}. {step.Trim()}"))
            : null;

    /// <summary>
    /// The stages, in order, with the run's budget checked between them: a run that has spent its time or
    /// tokens does not start the next stage. Design, Build and the judge and repair loop come after
    /// Understand as they are added.
    /// </summary>
    private async Task<UltraTeamEndReason> RunStagesAsync(
        string goal, string? approvedPlan, RunWorkspace workspace, WorkspaceWorktree readOnly, CancellationToken cancellationToken)
    {
        var readDirectory = Path.GetFullPath(Path.Combine(readOnly.Path, workspace.SolutionRelativeDirectory));
        var conductor = new PipelineConductor(
            _leadHost, _readOnlyTools, readDirectory, model: null, _options.OrchestratorEffort);

        await RosterStageAsync(conductor, goal, approvedPlan, cancellationToken);
        if (Spent() is { } afterRoster)
        {
            return afterRoster;
        }

        await UnderstandStageAsync(goal, readDirectory, cancellationToken);
        return Spent() ?? UltraTeamEndReason.AllDone;
    }

    /// <summary>Why the run cannot start another stage, or null while it has time and tokens left.</summary>
    private UltraTeamEndReason? Spent() =>
        !_budget.IsSpent ? null : _budget.TimeLeft <= TimeSpan.Zero ? UltraTeamEndReason.TimeUp : UltraTeamEndReason.BudgetSpent;

    // ---- Roster ----

    private async Task RosterStageAsync(
        PipelineConductor conductor, string goal, string? approvedPlan, CancellationToken cancellationToken)
    {
        SetStage("Roster - the lead is deciding who the run needs");
        using var clock = _budget.BeginStage(UltraStage.Roster);
        var limits = new RosterLimits(Math.Max(0, _options.PipelineMaxUnderstand), Math.Max(0, _options.PipelineMaxCustomJudges));
        var outcome = await conductor.DecideRosterAsync(goal, limits, approvedPlan, clock, cancellationToken);
        _roster = outcome.Plan;

        if (!outcome.Decided && outcome.Note is { } why)
        {
            Gap(why);
        }

        await RecordAsync("roster.json", RosterRecord.From(outcome.Plan, outcome.Decided, outcome.Note), cancellationToken);
        LogStage(clock, UltraStage.Roster, 0, [], outcome.Note);
    }

    // ---- Understand ----

    /// <summary>Surveyors read the code, each its own area, in parallel on the shared read-only checkout.
    /// A surveyor that does not submit is a gap in the record, and the run goes on without its survey.</summary>
    private async Task UnderstandStageAsync(string goal, string readDirectory, CancellationToken cancellationToken)
    {
        var surveyors = _roster.Surveyors;
        if (surveyors.Count == 0)
        {
            LogSkipped(UltraStage.Understand, "no surveyors: the designer surveys for itself");
            return;
        }

        SetStage($"Understand - {surveyors.Count} surveyor{(surveyors.Count == 1 ? "" : "s")} reading the code");
        using var clock = _budget.BeginStage(UltraStage.Understand);
        var surveys = surveyors.Select(s => new SurveyRun(s)).ToList();
        var outcomes = await _workerHost.RunAllAsync(
            [.. surveys.Select(survey => SurveyorAssignment(goal, readDirectory, survey))], clock, cancellationToken);

        for (var i = 0; i < surveys.Count; i++)
        {
            var survey = surveys[i];
            var outcome = outcomes[i];
            if (outcome.Gap is not null)
            {
                Gap(outcome.Gap);
            }

            await RecordAsync(
                $"survey/{survey.Surveyor.Agent.Name}.json",
                new SurveyRecord(
                    survey.Surveyor.Agent.Name, survey.Surveyor.Focus.Area, survey.Surveyor.Focus.Question,
                    survey.Report is not null, outcome.Gap, survey.Report),
                cancellationToken);
        }

        LogStage(clock, UltraStage.Understand, 0, outcomes, null);
    }

    /// <summary>What one surveyor hands in: set once, by <see cref="SubmitSurvey"/>.</summary>
    private sealed class SurveyRun(SurveyorAssignment surveyor)
    {
        public SurveyorAssignment Surveyor { get; } = surveyor;

        public SurveyReport? Report { get; set; }
    }

    private PipelineAgentAssignment SurveyorAssignment(string goal, string readDirectory, SurveyRun survey)
    {
        var agent = survey.Surveyor.Agent;
        var state = NewAgent(agent.Name);
        return new PipelineAgentAssignment
        {
            Name = agent.Name,
            SystemPrompt = PipelinePrompts.Surveyor(agent.Name, agent.Role),
            Opening = PipelinePrompts.SurveyorOpening(goal, survey.Surveyor.Focus),
            Tools = PipelineStageTools.ForSurveyor,
            SubmitToolName = PipelineStageTools.SurveySubmitToolName,
            IsComplete = () => survey.Report is not null,
            Effort = _options.WorkerEffort ?? agent.Role.DefaultEffort,
            MaxIterations = SurveyorIterations,
            State = state,
            ExecuteTool = async (tool, arguments, ct) =>
            {
                state.RecordStep(tool);
                return tool == PipelineStageTools.SurveySubmitToolName
                    ? SubmitSurvey(survey, arguments)
                    : await _readOnlyTools.ExecuteAsync(tool, arguments, readDirectory, ct);
            },
        };
    }

    private static (bool IsError, string Text) SubmitSurvey(SurveyRun survey, JsonNode? arguments)
    {
        var tool = PipelineStageTools.SurveySubmitToolName;
        if (survey.Report is not null)
        {
            return (true, "The survey is already recorded. You are done - stop.");
        }

        var parsed = PipelineToolArguments.Parse<SurveyReport>(arguments, tool);
        if (parsed.Value is null)
        {
            return (true, PipelinePrompts.NotRecorded(tool, parsed.Feedback));
        }

        // The area is the one the surveyor was given; it need not say it back.
        var submitted = string.IsNullOrWhiteSpace(parsed.Value.Area) ? parsed.Value with { Area = survey.Surveyor.Focus.Area } : parsed.Value;
        var check = SurveyValidator.Validate(submitted);
        if (!check.IsValid)
        {
            return (true, PipelinePrompts.NotRecorded(tool, check.Feedback));
        }

        survey.Report = check.Value;
        return (false, "Survey recorded. You are done.");
    }

    // ---- Records and status ----

    private TeamWorkerState NewAgent(string name)
    {
        var state = new TeamWorkerState(name, _time);
        lock (_lock)
        {
            _agents.Add(state);
        }

        OnChanged();
        return state;
    }

    private void LogStage(
        StageClock clock, UltraStage stage, int round, IReadOnlyList<PipelineAgentOutcome> outcomes, string? note)
    {
        var record = new StageRecord(
            stage.ToString(), round, clock.Elapsed.TotalSeconds, clock.TokensUsed,
            [.. outcomes.Select(o => new AgentRecord(
                o.Name, o.End.ToString(), o.Gap, o.Reminders, o.WrapUp?.ToString(),
                o.Iterations, o.ToolCalls, o.PromptTokens, o.CompletionTokens))],
            note);
        lock (_lock)
        {
            _stages.Add(record);
        }
    }

    /// <summary>A stage the run had nothing to do in - recorded, so the record says it was left out on purpose.</summary>
    private void LogSkipped(UltraStage stage, string why)
    {
        lock (_lock)
        {
            _stages.Add(new StageRecord(stage.ToString(), 0, 0, 0, [], $"skipped: {why}"));
        }
    }

    private void Gap(string text)
    {
        lock (_lock)
        {
            _gaps.Add(text);
        }
    }

    /// <summary>
    /// Writes one record. A record that cannot be written - the disk is full, something holds the file -
    /// is a gap in the summary and nothing more: the records are for reading afterwards, and a run that
    /// produced code is not lost for the want of one of them.
    /// </summary>
    private async Task RecordAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
    {
        try
        {
            await _phases!.WriteJsonAsync(relativePath, value, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Gap($"could not write {relativePath}: {ex.Message}");
        }
    }

    private async Task WriteSummaryAsync(UltraTeamEndReason endReason)
    {
        List<StageRecord> stages;
        List<string> gaps;
        lock (_lock)
        {
            stages = [.. _stages];
            gaps = [.. _gaps];
        }

        await RecordAsync(
            "summary.json",
            new RunSummaryRecord(
                RunId, _goal, endReason.ToString(), Elapsed.TotalSeconds, PromptTokens, CompletionTokens, stages, gaps),
            CancellationToken.None);
    }

    private void SetSetup(string? status)
    {
        _setupStatus = status;
        OnChanged();
    }

    private void SetStage(string? text)
    {
        _stageText = text;
        OnChanged();
    }

    private void OnChanged() => Changed?.Invoke();

    /// <summary>Adds a response's usage to the run's totals as it arrives (see <see cref="UsageCountingChatClient"/>),
    /// so the card's count moves during a stage and the budget sees what has been spent.</summary>
    private void OnUsage(UsageReceived usage)
    {
        Interlocked.Add(ref _promptTokens, usage.PromptTokens);
        Interlocked.Add(ref _completionTokens, usage.CompletionTokens);
        Interlocked.Add(ref _cachedPromptTokens, usage.CachedPromptTokens);
        OnChanged();
    }
}
