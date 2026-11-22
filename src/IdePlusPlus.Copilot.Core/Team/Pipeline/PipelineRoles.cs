namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>Which of the pipeline's agents a <see cref="TeamRole"/> is.</summary>
public enum PipelineRoleKind
{
    /// <summary>Surveys an area of the code and reports; changes nothing.</summary>
    Understander,

    /// <summary>The one agent that turns the surveys into a plan and commits the interface stubs.</summary>
    Designer,

    /// <summary>Owns a set of files: builds them, repairs them, and answers requests to change them.</summary>
    Owner,

    /// <summary>Reads what was built and returns a verdict with findings.</summary>
    Judge,
}

/// <summary>An agent of the pipeline: its name (u1, d1, b2, j3) and the role it plays.</summary>
public sealed record PipelineAgent(string Name, TeamRole Role);

/// <summary>
/// The pipeline's roles. Unlike <see cref="TeamRoles"/> this is a factory, not a catalogue: the
/// orchestrator decides how many surveyors there are and may define judges of its own, so the roster
/// is made per run. Nothing here is added to <see cref="TeamRoles.All"/>, which keeps the original
/// team's roster, its settings and its review queue exactly as they were.
///
/// Every role is a <see cref="TeamRole"/> with <see cref="TeamRoleKind.Pipeline"/>, so what an agent
/// may do is decided by its edit scope and <see cref="TeamRole.AllowsMerge"/> - never by its charter.
/// A charter is prompt text; the orchestrator can write one for a custom judge, and nothing in it can
/// widen what that judge's tools are.
/// </summary>
public static class PipelineRoles
{
    /// <summary>The judges every pipeline run has: j1 to j4. Custom judges are numbered from five.</summary>
    public const int StandardJudgeCount = 4;

    public const int FirstCustomJudgeNumber = StandardJudgeCount + 1;

    public const int MaxJudgeTitleLength = 60;

    /// <summary>The longest charter an orchestrator-defined judge may have. The standard judges' own
    /// charters are longer; this is a cap on text a model writes into another model's prompt.</summary>
    public const int MaxCustomCharterLength = 800;

    public static TeamRole Understander { get; } = new(
        TeamRoleKind.Pipeline,
        "u",
        "Understander",
        "Your job is to understand, not to change. You are one of several surveyors and the orchestrator gave you "
        + "a focus area. Read the code, tests, docs and conventions in that area and report three things. What "
        + "already exists that the goal can reuse: patterns, helpers, naming, error handling and testing "
        + "conventions, each with the file that shows it. What does not exist yet but the goal needs, with the "
        + "reason. What looks dead, duplicated or over-built and could be trimmed, with the evidence: who calls "
        + "it, how many references. Back every claim with a file path. Trimming is a recommendation - say what "
        + "and why, and never act on it. Stay inside your focus area and do not design the solution: the "
        + "designer does that with every survey in front of it.",
        TeamRoleEditScope.None,
        TeamRoleTrigger.Stage,
        ChatEffort.Medium)
    {
        AllowsMerge = false,
        PipelineKind = PipelineRoleKind.Understander,
    };

    public static TeamRole Designer { get; } = new(
        TeamRoleKind.Pipeline,
        "d",
        "Designer",
        "You turn the surveys into a build plan that several owners can carry out at once and that fits together "
        + "at its seams. Decide the files and give every file exactly one owner. Write the interface contracts as "
        + "stubs that compile - signatures, types and members, with bodies that throw NotImplementedException - and "
        + "commit them first, so every owner builds against the same contract. For each file give acceptance "
        + "criteria that an owner and a judge can check. List the misuse cases a careless or hostile user could try "
        + "(empty, huge, malformed or concurrent input, the wrong user) so the security judge can check each one. "
        + "Put a survey's trim candidate in the plan only when the goal needs it; everything else is a suggested "
        + "next prompt. Prefer a few owners with coherent clusters of files to many owners with one file each.",
        TeamRoleEditScope.Any,
        TeamRoleTrigger.Stage,
        ChatEffort.High)
    {
        // The designer's commit is the contract every owner builds against, so the runner lands it - after
        // checking that it changes only the stubs the design declares, and builds. A designer that could
        // team_merge would put it on the team branch before that check.
        AllowsMerge = false,
        PipelineKind = PipelineRoleKind.Designer,
    };

    public static TeamRole Owner { get; } = new(
        TeamRoleKind.Pipeline,
        "b",
        "Owner",
        "You own the files the design gave you, and nobody else may edit them: build them to the contract, merge "
        + "small and early, and repair what the judges find in them. You may edit only the files you own. If "
        + "another file has to change for your work to fit, do not edit it - call request_change to ask its owner, "
        + "then await_change for the answer. When a peer asks you for a change, answer it before you do anything "
        + "else: accept it and make the change, or reject it with your reasoning. A rejection is a legitimate "
        + "answer. Never bend an interface contract on your own; a contract that is wrong is a request, not an edit.",
        TeamRoleEditScope.OwnedFilesOnly,
        TeamRoleTrigger.Stage,
        ChatEffort.Medium)
    {
        PipelineKind = PipelineRoleKind.Owner,
    };

    /// <summary>
    /// j1 to j4: gaps and flaws, maintainability, security with what-ifs, and runtime bugs. Only j4 may
    /// write anything, and only tests.
    /// </summary>
    public static IReadOnlyList<PipelineAgent> StandardJudges { get; } =
    [
        Create(1, "Gaps and flaws judge", GapsAndFlawsCharter, TeamRoleEditScope.None),
        Create(2, "Maintainability judge", MaintainabilityCharter, TeamRoleEditScope.None),
        Create(3, "Security and what-ifs judge", SecurityCharter, TeamRoleEditScope.None),
        Create(4, "Runtime bugs judge", RuntimeBugsCharter, TeamRoleEditScope.TestsOnly),
    ];

    /// <summary>The surveyor named u<paramref name="number"/>.</summary>
    public static PipelineAgent UnderstanderAgent(int number) => Named(Understander, number);

    /// <summary>The file owner named b<paramref name="number"/>.</summary>
    public static PipelineAgent OwnerAgent(int number) => Named(Owner, number);

    /// <summary>The designer, d1: there is exactly one.</summary>
    public static PipelineAgent DesignerAgent { get; } = new(Designer.NameFor(1), Designer);

    /// <summary>
    /// A judge the orchestrator defined. Its scope is limited to what a judge may ever do - read
    /// (<see cref="TeamRoleEditScope.None"/>) or write tests (<see cref="TeamRoleEditScope.TestsOnly"/>) -
    /// so no charter, however it is worded, can make a custom judge able to edit production code.
    /// </summary>
    /// <param name="number">From <see cref="FirstCustomJudgeNumber"/> up; the name is j&lt;number&gt;.</param>
    /// <exception cref="ArgumentException">For a scope that is not None or TestsOnly, a blank or too long
    /// title or charter, or a number that would collide with a standard judge.</exception>
    public static PipelineAgent CustomJudge(int number, string title, string charter, TeamRoleEditScope scope)
    {
        if (number < FirstCustomJudgeNumber)
        {
            throw new ArgumentOutOfRangeException(
                nameof(number), $"Custom judges are numbered from {FirstCustomJudgeNumber}: j1 to j{StandardJudgeCount} are the standard judges.");
        }

        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > MaxJudgeTitleLength)
        {
            throw new ArgumentException($"A judge's title is required and at most {MaxJudgeTitleLength} characters.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(charter) || charter.Trim().Length > MaxCustomCharterLength)
        {
            throw new ArgumentException($"A judge's charter is required and at most {MaxCustomCharterLength} characters.", nameof(charter));
        }

        return Create(number, title.Trim(), charter.Trim(), scope);
    }

    private static PipelineAgent Create(int number, string title, string charter, TeamRoleEditScope scope)
    {
        if (scope is not (TeamRoleEditScope.None or TeamRoleEditScope.TestsOnly))
        {
            throw new ArgumentOutOfRangeException(
                nameof(scope), scope, "A judge reads or writes tests; it never edits the code it judges.");
        }

        var role = new TeamRole(
            TeamRoleKind.Pipeline, "j", title, charter, scope, TeamRoleTrigger.Stage, ChatEffort.High)
        {
            AllowsMerge = false,
            PipelineKind = PipelineRoleKind.Judge,
        };
        return new PipelineAgent(role.NameFor(number), role);
    }

    private static PipelineAgent Named(TeamRole role, int number) =>
        number >= 1
            ? new PipelineAgent(role.NameFor(number), role)
            : throw new ArgumentOutOfRangeException(nameof(number), "Agent numbers start at 1.");

    private const string GapsAndFlawsCharter =
        "Your job is what is missing or wrong against the goal and the design. Compare the goal, the design's "
        + "acceptance criteria and the code: requirements nothing implements, call sites nobody updated, settings or "
        + "UI that were never wired, tests and docs the plan named that are missing, contracts a file does not "
        + "honour, logic that does the wrong thing. Report only what you can show: every medium or high finding "
        + "quotes the exact code and names the file.";

    private const string MaintainabilityCharter =
        "Your job is whether the next person can live with this code. Look for duplication of something the surveys "
        + "said already has a home, code that ignores the conventions the surveys found, names that mislead, "
        + "functions and classes doing too much, hidden coupling and global state, comments that are missing where "
        + "the why is not obvious, and tests that would not catch a regression. Prefer a few specific findings with "
        + "a concrete fix to a list of taste. Style a formatter would fix is low severity.";

    private const string SecurityCharter =
        "Your job is what a careless or hostile user could do. First work through the design's misuse cases one by "
        + "one and report each as covered, partly covered or uncovered, with evidence. Then your own what-ifs: null "
        + "or empty input, boundaries and off-by-one, cancellation partway through, concurrency and reentrancy, "
        + "disposal and leaked resources, failure paths, very large input, culture and encoding. Security: path "
        + "traversal, injection (shell, SQL, regex, XAML), secrets in code or logs, unvalidated external input, "
        + "unsafe deserialisation, time-of-check-to-time-of-use, privilege. You read and report; you do not write "
        + "tests.";

    private const string RuntimeBugsCharter =
        "Your job is the bugs that only show when the code runs: exceptions on realistic input, deadlocks and races, "
        + "wrong async and await, resources not released, off-by-one, the wrong culture, state that outlives its "
        + "owner. Prove each one. You may write tests, and only tests: a high severity finding needs a failing test "
        + "committed on your own branch and quoted as evidence=branch:<sha>; it travels with the finding and is "
        + "never merged. A bug you cannot demonstrate is at most medium.";
}
