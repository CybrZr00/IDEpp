using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>The roster the orchestrator chose. <see cref="Decided"/> is false when it could not: the
/// plan is then empty - the designer surveys for itself - and <see cref="Note"/> says why.</summary>
internal sealed record RosterOutcome(RosterPlan Plan, bool Decided, string? Note);

/// <summary>The orchestrator's triage. When it is not <see cref="Decided"/> the plan is empty and every
/// finding is left for the runner's own rule.</summary>
internal sealed record TriageOutcome(TriagePlan Plan, bool Decided, string? Note);

/// <summary>The ruling on an appeal. When the orchestrator could not give one the owner's refusal stands
/// and <see cref="Decided"/> is false.</summary>
internal sealed record ArbitrationOutcome(ArbitrationRuling Ruling, bool Decided, string? Note);

/// <summary>
/// The orchestrator of an Ultra Pipeline run - "lead" - at the three points where the run needs
/// judgement rather than a rule: which surveyors and extra judges the goal calls for, what to do with the
/// findings no rule can place, and who is right when a requester appeals an owner's refusal. The runner
/// does everything else itself, so between these calls the lead costs nothing.
///
/// Each call is a short conversation of its own with the lead's own model and effort, run through the
/// same <see cref="PipelineAgentHost"/> as every other agent: it is reminded to submit, asked to wrap up
/// when its stage's time or tokens run out, resumed once after a failure, and not started at all when
/// its stage has nothing left. It reads code with the surveyors' read-only tools and edits nothing.
///
/// It must never be the reason a run stops. A decision it cannot give comes back <c>Decided = false</c>
/// with a safe default - no extra agents, nothing triaged, the owner's refusal standing - and a note for
/// the record. Only the run's own cancellation propagates.
/// </summary>
/// <param name="host">Runs the conversations.</param>
/// <param name="readOnlyTools">What the lead reads code with.</param>
/// <param name="readDirectory">The checkout those tools read: the read-only one, as it stood when the
/// current stage began.</param>
/// <param name="model">The lead's model; null for the run's.</param>
/// <param name="effort">The lead's reasoning effort.</param>
internal sealed class PipelineConductor(
    PipelineAgentHost host,
    ReadOnlyToolDispatcher readOnlyTools,
    string readDirectory,
    string? model,
    ChatEffort effort)
{
    /// <summary>How many times a roster that breaks the rules is sent back before it is cut down to
    /// fit. The lead is told twice what is wrong; the third time what can be used is used.</summary>
    internal const int MaxRosterRefusals = 2;

    private const int RosterIterations = 15;
    private const int TriageIterations = 10;
    private const int ArbitrationIterations = 6;

    private static RosterPlan NoRoster { get; } = new([], [], null);

    private static ArbitrationRuling OwnerStands { get; } = new(
        RequesterWins: false,
        "The orchestrator could not rule on this appeal, so the owner's refusal stands. Carry on without the change, "
        + "or work round it - and say in your final summary what it would have needed.");

    // ---- Roster ----

    /// <summary>
    /// Decides who is on the roster, within <paramref name="limits"/>. With nothing allowed there is
    /// nothing to decide, and no request is made.
    /// </summary>
    /// <param name="approvedPlan">The steps the user approved in Plan Mode, if there were any.</param>
    public async Task<RosterOutcome> DecideRosterAsync(
        string goal, RosterLimits limits, string? approvedPlan, StageClock clock, CancellationToken cancellationToken)
    {
        if (limits.MaxUnderstand <= 0 && limits.MaxCustomJudges <= 0)
        {
            return new RosterOutcome(NoRoster, Decided: true, "No surveyors or custom judges are allowed in this run.");
        }

        RosterPlan? accepted = null;
        string? adjusted = null;
        var refusals = 0;

        (bool IsError, string Text) Submit(JsonNode? arguments)
        {
            if (accepted is not null)
            {
                return (true, AlreadyRecorded("roster"));
            }

            var parsed = PipelineToolArguments.Parse<RosterDecision>(arguments, PipelineLeadTools.SubmitRosterToolName);
            if (parsed.Value is null)
            {
                return (true, PipelinePrompts.NotRecorded(PipelineLeadTools.SubmitRosterToolName, parsed.Feedback));
            }

            var check = RosterValidator.Validate(parsed.Value, limits);
            if (check.IsValid)
            {
                accepted = check.Value;
                return (false, Recorded(accepted!));
            }

            if (++refusals <= MaxRosterRefusals)
            {
                return (true, PipelinePrompts.NotRecorded(PipelineLeadTools.SubmitRosterToolName, check.Feedback));
            }

            // The lead is not told: it stops here, and the change is in the outcome's note for the record.
            (accepted, adjusted) = Salvage(parsed.Value, limits);
            return (false, Recorded(accepted));
        }

        var outcome = await RunAsync(
            ConductorPrompts.Roster(limits),
            ConductorPrompts.RosterOpening(goal, approvedPlan),
            PipelineLeadTools.ForRoster,
            PipelineLeadTools.SubmitRosterToolName,
            RosterIterations,
            Submit,
            () => accepted is not null,
            clock,
            cancellationToken);

        return accepted is { } plan
            ? new RosterOutcome(plan, Decided: true, adjusted is null ? null : $"The roster was changed: {adjusted}.")
            : new RosterOutcome(NoRoster, Decided: false, $"No roster was decided ({outcome.Gap}); the designer surveys for itself.");
    }

    /// <summary>
    /// What can be kept of a roster that is still wrong after the lead has been told. It is cut to the
    /// limits first; if that is not enough, the custom judges go, then the surveyors instead of them, and
    /// last everything - an empty roster is always allowed. Whatever survives is used as submitted.
    /// </summary>
    internal static (RosterPlan Plan, string Adjusted) Salvage(RosterDecision decision, RosterLimits limits)
    {
        var clamped = RosterValidator.Clamp(decision, limits);
        (RosterDecision Candidate, string Adjusted)[] rungs =
        [
            (clamped, "it was cut down to the limits"),
            (clamped with { CustomJudges = [] }, "its custom judges were left out"),
            (clamped with { Surveyors = [] }, "its surveyors were left out"),
        ];

        foreach (var (candidate, adjusted) in rungs)
        {
            if (RosterValidator.Validate(candidate, limits) is { IsValid: true, Value: { } plan })
            {
                return (plan, adjusted);
            }
        }

        return (NoRoster, "none of it could be used, so the roster is empty");
    }

    // ---- Triage ----

    /// <summary>
    /// Decides what becomes of <paramref name="findings"/>, the ones the runner's own rule cannot place:
    /// every one is assigned to an owner, dismissed or deferred to the user.
    /// </summary>
    public async Task<TriageOutcome> TriageAsync(
        string goal,
        IReadOnlyList<LedgerFinding> findings,
        OwnershipMap ownership,
        StageClock clock,
        CancellationToken cancellationToken)
    {
        if (findings.Count == 0)
        {
            return new TriageOutcome(TriagePlan.Empty, Decided: true, "There was nothing to triage.");
        }

        TriagePlan? accepted = null;

        (bool IsError, string Text) Submit(JsonNode? arguments)
        {
            if (accepted is not null)
            {
                return (true, AlreadyRecorded("triage"));
            }

            var parsed = PipelineToolArguments.Parse<TriageSubmission>(arguments, PipelineLeadTools.SubmitTriageToolName);
            if (parsed.Value is null)
            {
                return (true, PipelinePrompts.NotRecorded(PipelineLeadTools.SubmitTriageToolName, parsed.Feedback));
            }

            var check = TriageValidator.Validate(parsed.Value, findings, ownership);
            if (!check.IsValid)
            {
                return (true, PipelinePrompts.NotRecorded(PipelineLeadTools.SubmitTriageToolName, check.Feedback));
            }

            accepted = check.Value;
            return (false, $"Triage recorded: {accepted!.Decisions.Count} decision(s) for {findings.Count} finding(s). You are done.");
        }

        var outcome = await RunAsync(
            ConductorPrompts.Triage(),
            ConductorPrompts.TriageOpening(goal, findings, ownership),
            PipelineLeadTools.ForTriage,
            PipelineLeadTools.SubmitTriageToolName,
            TriageIterations,
            Submit,
            () => accepted is not null,
            clock,
            cancellationToken);

        return accepted is { } plan
            ? new TriageOutcome(plan, Decided: true, null)
            : new TriageOutcome(TriagePlan.Empty, Decided: false, $"No triage was decided ({outcome.Gap}).");
    }

    // ---- Appeals ----

    /// <summary>
    /// Rules on an appealed change request, once and for good. <paramref name="design"/> is whatever of
    /// the design bears on the file, if the caller has it. The caller applies the ruling to the registry;
    /// this only decides.
    /// </summary>
    public async Task<ArbitrationOutcome> ArbitrateAsync(
        string goal, ChangeRequest request, string? design, StageClock clock, CancellationToken cancellationToken)
    {
        ArbitrationRuling? accepted = null;

        (bool IsError, string Text) Submit(JsonNode? arguments)
        {
            if (accepted is not null)
            {
                return (true, AlreadyRecorded("ruling"));
            }

            var parsed = PipelineToolArguments.Parse<ArbitrationSubmission>(arguments, PipelineLeadTools.ArbitrateToolName);
            if (parsed.Value is null)
            {
                return (true, PipelinePrompts.NotRecorded(PipelineLeadTools.ArbitrateToolName, parsed.Feedback));
            }

            var check = ArbitrationValidator.Validate(parsed.Value, request.Id);
            if (!check.IsValid)
            {
                return (true, PipelinePrompts.NotRecorded(PipelineLeadTools.ArbitrateToolName, check.Feedback));
            }

            accepted = check.Value;
            return (false, $"Ruling recorded for {(accepted!.RequesterWins ? request.From : request.To)}. You are done.");
        }

        var outcome = await RunAsync(
            ConductorPrompts.Arbitration(),
            ConductorPrompts.ArbitrationOpening(goal, request, design),
            PipelineLeadTools.ForArbitration,
            PipelineLeadTools.ArbitrateToolName,
            ArbitrationIterations,
            Submit,
            () => accepted is not null,
            clock,
            cancellationToken);

        return accepted is { } ruling
            ? new ArbitrationOutcome(ruling, Decided: true, null)
            : new ArbitrationOutcome(OwnerStands, Decided: false, $"No ruling was given on {request.Id} ({outcome.Gap}); the owner's refusal stands.");
    }

    // ---- Shared ----

    private Task<PipelineAgentOutcome> RunAsync(
        string systemPrompt,
        string opening,
        IReadOnlyList<ToolDefinition> tools,
        string submitTool,
        int iterations,
        Func<JsonNode?, (bool IsError, string Text)> submit,
        Func<bool> isComplete,
        StageClock clock,
        CancellationToken cancellationToken) =>
        host.RunAsync(
            new PipelineAgentAssignment
            {
                Name = LeadTools.LeadName,
                SystemPrompt = systemPrompt,
                Opening = opening,
                Tools = tools,
                SubmitToolName = submitTool,
                IsComplete = isComplete,
                Model = model,
                Effort = effort,
                MaxIterations = iterations,
                ExecuteTool = async (tool, arguments, ct) => tool == submitTool
                    ? submit(arguments)
                    : await readOnlyTools.ExecuteAsync(tool, arguments, readDirectory, ct),
            },
            clock,
            cancellationToken);

    private static string Recorded(RosterPlan plan) =>
        $"Roster recorded: {plan.Surveyors.Count} surveyor(s), {plan.CustomJudges.Count} custom judge(s). You are done.";

    private static string AlreadyRecorded(string what) => $"The {what} is already recorded. You are done - stop.";
}
