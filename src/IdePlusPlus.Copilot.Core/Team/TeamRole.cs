namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>
/// What a worker is on the team. Builders take the goal's TASKs; the specialists each run their own
/// lane over what the builders merge - an Agensh-style team of identical workers by definition does
/// not delegate, and a fixed roster of roles is the smallest thing that makes the review work happen
/// beside the building rather than after it.
/// </summary>
public enum TeamRoleKind
{
    Builder,
    MutationTester,
    Adversary,
    Conformity,
    GapFinder,
    FollowUpScout,
}

/// <summary>
/// What a role may change in its own worktree. Hidden tools are not enough - a model that was not
/// offered edit_file will still try it once it has seen a peer do it - so the dispatcher checks this
/// as well and refuses the write.
/// </summary>
public enum TeamRoleEditScope
{
    /// <summary>Anywhere in the worktree, the way a builder has always worked.</summary>
    Any,

    /// <summary>Only under the run's test projects: the adversary and the mutation tester write tests
    /// and nothing else, so a suite that passes still means the production code was not bent to fit it.</summary>
    TestsOnly,

    /// <summary>Only the files the review item a specialist is on changed - the conformity lane's job
    /// is the merged diff's style, not the rest of the tree.</summary>
    ChangedFilesStyleOnly,

    /// <summary>Nothing: the role reads, reports and merges nothing.</summary>
    None,
}

/// <summary>What wakes a role up: the backlog the lead (or the first worker) wrote, or the queue of
/// merges that have landed since the role last looked.</summary>
public enum TeamRoleTrigger
{
    Backlog,
    MergeQueue,
}

/// <param name="NamePrefix">The first part of a worker's name: b, mut, adv, lint, gap, next - so the
/// board, the channel and the review queue say which lane a message came from.</param>
/// <param name="Charter">What the role is for, in the words its prompt uses.</param>
public sealed record TeamRole(
    TeamRoleKind Kind,
    string NamePrefix,
    string DisplayName,
    string Charter,
    TeamRoleEditScope EditScope,
    TeamRoleTrigger Trigger,
    ChatEffort DefaultEffort)
{
    /// <summary>True for every role but the builder: specialists review merged work rather than build.</summary>
    public bool IsSpecialist => Kind != TeamRoleKind.Builder;

    public bool CanEdit => EditScope != TeamRoleEditScope.None;

    /// <summary>A role with no edits has no branch to merge, and the adversary's failing test is
    /// evidence rather than a fix - it travels with the finding, so nothing of the adversary's is
    /// ever merged.</summary>
    public bool CanMerge => CanEdit && Kind != TeamRoleKind.Adversary;

    public string NameFor(int number) => $"{NamePrefix}{number}";
}

/// <summary>The fixed roster. Every role the team can be built from, and the prefixes its workers
/// are named with.</summary>
public static class TeamRoles
{
    public static TeamRole Builder { get; } = new(
        TeamRoleKind.Builder,
        "b",
        "Builder",
        "Take TASKs from the board one at a time and merge each one small and early: the specialists review, "
        + "mutation-test and lint what lands, so they only get work when something merges. Build what the goal "
        + "asks for and nothing more. Do not do the specialists' jobs yourself - if a merge raises a question "
        + "about behaviour, tests or style, the adversary, the mutation tester and the conformity lane own it.",
        TeamRoleEditScope.Any,
        TeamRoleTrigger.Backlog,
        ChatEffort.Medium);

    public static TeamRole MutationTester { get; } = new(
        TeamRoleKind.MutationTester,
        "mut",
        "Mutation tester",
        "Your lane is the mutation score of merged work. Take the oldest merge waiting for your lane and run "
        + "run_mutation_tests over the files it changed. For every mutant that survived, write the test that kills "
        + "it - tests only, that is your whole edit scope - merge the tests, and post a FINDING with "
        + "severity=low kind=mutation score=k/n naming the merge you reviewed. Never change production code: an "
        + "uncovered behaviour is reported, not patched.",
        TeamRoleEditScope.TestsOnly,
        TeamRoleTrigger.MergeQueue,
        ChatEffort.Medium);

    public static TeamRole Adversary { get; } = new(
        TeamRoleKind.Adversary,
        "adv",
        "Adversary",
        "Your lane is what breaks merged work. Read the merge and the code around it, then work two checklists. "
        + "What-ifs: null or empty input, boundaries and off-by-one, cancellation partway through, concurrency and "
        + "reentrancy, disposal and leaked resources, failure paths, very large input, culture and encoding. "
        + "Security: path traversal, injection (shell, SQL, regex, XAML), secrets in code or logs, unvalidated "
        + "external input, unsafe deserialisation, time-of-check-to-time-of-use, privilege. severity=high needs a "
        + "failing test committed in your own branch: quote it as evidence=branch:<sha>, and it travels with the "
        + "finding rather than being merged. Anything else is at most severity=medium. Say who fixes it with "
        + "fix_by=builders|me|user.",
        TeamRoleEditScope.TestsOnly,
        TeamRoleTrigger.MergeQueue,
        ChatEffort.High);

    public static TeamRole Conformity { get; } = new(
        TeamRoleKind.Conformity,
        "lint",
        "Conformity",
        "Your lane is the merged diff's style. check_conformity(files) runs dotnet format style and the analyzers "
        + "in verify mode over the files the merge changed, plus a build whose warnings for those files are kept. "
        + "Fix style-only problems yourself - your edit scope is exactly the files the review item changed - and "
        + "merge the fixes in small batches. Anything that would change behaviour is a FINDING, not a fix.",
        TeamRoleEditScope.ChangedFilesStyleOnly,
        TeamRoleTrigger.MergeQueue,
        ChatEffort.Low);

    public static TeamRole GapFinder { get; } = new(
        TeamRoleKind.GapFinder,
        "gap",
        "Gap finder",
        "You run against the goal, the plan and the board rather than a single diff. After every few merges, and "
        + "once at wrap-up, compare the goal, the plan TASKs and the PATCH_SUMMARYs with the code: find_references "
        + "on each changed public signature for call sites nobody updated, settings or UI that were never wired, "
        + "tests and docs the plan named that are missing. Write lane=gap TASKs for the builders - each one inside "
        + "the goal - rather than fixing anything yourself.",
        TeamRoleEditScope.None,
        TeamRoleTrigger.MergeQueue,
        ChatEffort.Medium);

    public static TeamRole FollowUpScout { get; } = new(
        TeamRoleKind.FollowUpScout,
        "next",
        "Follow-up scout",
        "You run against the goal and the board, and you never edit, never merge and never add a TASK: the team "
        + "only does work inside the goal. Collect what is worth doing but out of scope as FINDINGs with "
        + "kind=followup - the run's result turns them into a 'Suggested next prompts' list for the user. Be "
        + "concrete: say what to do and where.",
        TeamRoleEditScope.None,
        TeamRoleTrigger.MergeQueue,
        ChatEffort.Low);

    /// <summary>Every role, builders first, then the specialist lanes in the order the settings, the
    /// board digest and the result report show them.</summary>
    public static IReadOnlyList<TeamRole> All { get; } =
        [Builder, MutationTester, Adversary, Conformity, GapFinder, FollowUpScout];

    public static IReadOnlyList<TeamRole> Specialists { get; } = [.. All.Where(r => r.IsSpecialist)];

    public static TeamRole For(TeamRoleKind kind) => All.First(role => role.Kind == kind);

    public static string NameFor(TeamRoleKind kind, int number) => For(kind).NameFor(number);

    /// <summary>
    /// The role a worker's name belongs to ("mut2" is a mutation tester), or null for a name that
    /// follows the older w&lt;number&gt; scheme or none of the prefixes at all.
    /// </summary>
    public static TeamRole? ForWorkerName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        name = name.Trim();
        foreach (var role in All.OrderByDescending(r => r.NamePrefix.Length))
        {
            if (name.Length > role.NamePrefix.Length
                && name.StartsWith(role.NamePrefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name[role.NamePrefix.Length..], out var number)
                && number >= 1)
            {
                return role;
            }
        }

        return null;
    }
}

/// <summary>Which preset a team is built from. Speed is the roster Ultra shipped with - builders
/// only - and stays the default, so an existing run behaves exactly as it did.</summary>
public enum TeamCompositionPreset
{
    Speed,
    Balanced,
    Hardening,
}

/// <summary>
/// A whole team: how many builders, and how many of each specialist lane. Builders are a first-class
/// count rather than one more entry in the dictionary because the composition exists to answer "is
/// there enough building to keep the specialists fed", and an empty dictionary is then the honest
/// description of Speed rather than a list of zeroes.
/// </summary>
public sealed record TeamComposition(int Builders, IReadOnlyDictionary<TeamRoleKind, int> Specialists)
{
    /// <summary>The most workers one run may start. Matches Ultra's own setting clamp.</summary>
    public const int MaxUltraWorkerCount = 64;

    /// <summary>At most this many of one specialist lane: past two, a lane's work is a queue of its
    /// own and the second worker is already idle on it.</summary>
    public const int MaxSpecialistsPerKind = 2;

    private static readonly IReadOnlyDictionary<TeamRoleKind, int> None = new Dictionary<TeamRoleKind, int>();

    public TeamComposition(int builders)
        : this(builders, None)
    {
    }

    /// <summary>Builders only - today's behaviour, and the default preset.</summary>
    public static TeamComposition Speed(int builders = 4) => new(builders, None);

    /// <summary>One of each review lane: the smallest roster that covers mutation, adversarial,
    /// conformity and gap work at once.</summary>
    public static TeamComposition Balanced(int builders = 3) => new(builders, new Dictionary<TeamRoleKind, int>
    {
        [TeamRoleKind.MutationTester] = 1,
        [TeamRoleKind.Adversary] = 1,
        [TeamRoleKind.Conformity] = 1,
        [TeamRoleKind.GapFinder] = 1,
    });

    /// <summary>Two mutation testers and two adversaries, for a goal where correctness matters more
    /// than throughput.</summary>
    public static TeamComposition Hardening(int builders = 2) => new(builders, new Dictionary<TeamRoleKind, int>
    {
        [TeamRoleKind.MutationTester] = 2,
        [TeamRoleKind.Adversary] = 2,
    });

    public static TeamComposition For(TeamCompositionPreset preset, int builders) => preset switch
    {
        TeamCompositionPreset.Balanced => Balanced(builders),
        TeamCompositionPreset.Hardening => Hardening(builders),
        _ => Speed(builders),
    };

    /// <summary>The settings and the default path build an all-builder team with this.</summary>
    public static TeamComposition AllBuilders(int builders) => Speed(builders);

    /// <summary>Every worker in the team, builders first, then each specialist lane in catalogue order.</summary>
    public int Total => Builders + Specialists.Values.Where(count => count > 0).Sum();

    public int SpecialistTotal => Total - Builders;

    public bool Has(TeamRoleKind kind) => CountOf(kind) > 0;

    public int CountOf(TeamRoleKind kind) =>
        kind == TeamRoleKind.Builder ? Builders : Specialists.GetValueOrDefault(kind);

    /// <summary>The role and name of every worker, in the order they are started: b1..bN, then
    /// mut1..., adv1..., lint1..., gap1..., next1... .</summary>
    public IReadOnlyList<(TeamRole Role, string Name)> Workers
    {
        get
        {
            var workers = new List<(TeamRole, string)>();
            foreach (var role in TeamRoles.All)
            {
                var count = CountOf(role.Kind);
                for (var number = 1; number <= count; number++)
                {
                    workers.Add((role, role.NameFor(number)));
                }
            }

            return workers;
        }
    }

    /// <summary>Null when the composition can be started; otherwise what is wrong with it, in the
    /// words the settings card shows.</summary>
    public string? Validate()
    {
        if (Builders < 1)
        {
            return "An Ultra team needs at least one builder - the specialists review work, they do not do it.";
        }

        foreach (var (kind, count) in Specialists)
        {
            if (kind == TeamRoleKind.Builder)
            {
                return "Builders are counted separately from the specialist lanes.";
            }

            if (count < 0)
            {
                return $"The {TeamRoles.For(kind).DisplayName} count cannot be negative.";
            }

            if (count > MaxSpecialistsPerKind)
            {
                return $"At most {MaxSpecialistsPerKind} {TeamRoles.For(kind).DisplayName.ToLowerInvariant()} workers - "
                       + "a lane with more is a queue of its own.";
            }
        }

        return Total > MaxUltraWorkerCount
            ? $"That is {Total} workers; a run can start at most {MaxUltraWorkerCount}."
            : null;
    }
}
