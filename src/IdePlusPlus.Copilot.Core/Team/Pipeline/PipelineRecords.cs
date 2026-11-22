namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

// What the runner writes to the run folder (<run>\_phases) as it goes, so a run can be read afterwards -
// what the lead decided, what each surveyor found, how each stage went - and so a later loop can be
// told what an earlier one did. The runner writes every one of them; an agent never does. Plain data, in
// camelCase JSON through PipelineArtifactStore (which versions the file).

public sealed record RosterSurveyorRecord(string Name, string Area, string? Question);

public sealed record RosterJudgeRecord(string Name, string Title, string Charter, string Scope, IReadOnlyList<string> FocusFiles);

/// <summary>What the orchestrator chose (roster.json). <paramref name="Decided"/> is false when it could
/// not choose, in which case the roster is empty and <paramref name="Note"/> says why.</summary>
public sealed record RosterRecord(
    bool Decided,
    IReadOnlyList<RosterSurveyorRecord> Surveyors,
    IReadOnlyList<RosterJudgeRecord> CustomJudges,
    string? Rationale,
    string? Note)
{
    public static RosterRecord From(RosterPlan plan, bool decided, string? note) => new(
        decided,
        [.. plan.Surveyors.Select(s => new RosterSurveyorRecord(s.Agent.Name, s.Focus.Area, s.Focus.Question))],
        [.. plan.CustomJudges.Select(j => new RosterJudgeRecord(
            j.Agent.Name,
            j.Agent.Role.DisplayName,
            j.Agent.Role.Charter,
            j.Agent.Role.EditScope == TeamRoleEditScope.TestsOnly ? "testsOnly" : "none",
            j.FocusFiles))],
        plan.Rationale,
        note);
}

/// <summary>One surveyor's result (survey\u1.json): what it submitted, or why it did not.</summary>
public sealed record SurveyRecord(
    string Agent,
    string Area,
    string? Question,
    bool Submitted,
    string? Gap,
    SurveyReport? Report);

/// <summary>The accepted design (design.json), and the commit that carries its interface stubs when it has any.</summary>
public sealed record DesignRecord(DesignPlan Design, string? ContractCommit);

/// <summary>Who owns which file (ownership.json): the map the dispatchers enforce, as it stood when the
/// design was accepted.</summary>
public sealed record OwnershipRecord(IReadOnlyList<string> Owners, IReadOnlyDictionary<string, string> Files)
{
    public static OwnershipRecord From(OwnershipMap map) => new(map.Owners, map.Entries);
}

/// <summary>One owner's part of the build (build/bN.json): the files it owned, what it said it did, how much of it
/// reached the team branch, and why it gave no report if it did not.</summary>
public sealed record BuildRecord(
    string Owner,
    IReadOnlyList<string> Files,
    bool Submitted,
    string? Summary,
    IReadOnlyList<string> Unmet,
    int Merges,
    int Steps,
    string? Gap);

/// <summary>How one agent's run in a stage ended.</summary>
public sealed record AgentRecord(
    string Name,
    string End,
    string? Gap,
    int Reminders,
    string? WrapUp,
    int Iterations,
    int ToolCalls,
    int PromptTokens,
    int CompletionTokens);

/// <summary>One stage as it happened: how long it took, what it cost, who ran in it. A stage that did
/// not run says why in <paramref name="Note"/>.</summary>
public sealed record StageRecord(
    string Stage,
    int Round,
    double Seconds,
    long Tokens,
    IReadOnlyList<AgentRecord> Agents,
    string? Note = null);

/// <summary>The run in one file (summary.json): how it ended, what it cost, every stage, and every gap -
/// an agent that gave no result, a record that could not be written - in one list.</summary>
public sealed record RunSummaryRecord(
    string RunId,
    string Goal,
    string EndReason,
    double Seconds,
    long PromptTokens,
    long CompletionTokens,
    IReadOnlyList<StageRecord> Stages,
    IReadOnlyList<string> Gaps);
