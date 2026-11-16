using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>
/// What an Ultra worker can call. Definitions only - the handler is the per-worker team dispatcher
/// in the shell, which is where the worker's worktree, build service and git all are.
///
/// Like SubAgentTools, the existing tools a worker gets are a hard-coded allow-list rather than a
/// filtered copy of BuiltInTools.All: MCP and plugin tools are left out because nothing proves they
/// act only on the worker's worktree, and a future built-in tool stays out until someone decides it
/// belongs here. propose_edit/propose_plan are replaced by edit_file, which writes straight to the
/// worktree - nothing a worker does reaches the user until the run's result is reviewed.
/// </summary>
public static class TeamTools
{
    public const string BoardReadToolName = "board_read";
    public const string BoardWriteToolName = "board_write";
    public const string BoardGrepToolName = "board_grep";
    public const string BoardUnfoldToolName = "board_unfold";
    public const string PostToolName = "team_post";
    public const string DirectMessageToolName = "team_dm";
    public const string CommitToolName = "team_commit";
    public const string SyncToolName = "team_sync";
    public const string MergeToolName = "team_merge";
    public const string DoneToolName = "team_done";
    public const string RequestHelpToolName = "team_request_help";
    public const string WaitToolName = "team_wait";
    public const string EditFileToolName = "edit_file";
    public const string NextReviewToolName = "team_next_review";
    public const string ReportFindingToolName = "report_finding";
    public const string RunMutationTestsToolName = "run_mutation_tests";
    public const string CheckConformityToolName = "check_conformity";

    /// <summary>The most mutants one run_mutation_tests call tries: each one is a build and a test
    /// run, so a dozen is already several minutes of one worktree.</summary>
    public const int MaxMutantsPerRun = 12;

    public const int DefaultMutantsPerRun = 8;

    /// <summary>Most workers one help request can bring in - the team grows a little at a time.</summary>
    public const int MaxWorkersPerHelpRequest = 2;

    /// <summary>How long one team_wait may last by default, and at most. A long wait is not a long
    /// sleep: it ends the moment a peer merges, posts a FAIL or a TASK, or messages the worker.</summary>
    public const int DefaultWaitMinutes = 5;

    public const int MaxWaitMinutes = 15;

    public static readonly ToolDefinition BoardRead = new(
        Name: BoardReadToolName,
        Description: "Read the team board: every active CLAIM, then the latest entries. Read it before choosing " +
                     "work so you don't redo a peer's FACT, retry a FAIL, or take an area someone has claimed.",
        ParametersSchema: JsonNode.Parse("""{ "type": "object", "properties": {} }""")!);

    public static readonly ToolDefinition BoardWrite = new(
        Name: BoardWriteToolName,
        Description: "Add an entry to the team board. Types: OBSERVED (something you noticed), FACT (something you " +
                     "confirmed), FAIL (an approach that did not work, and why - the most valuable entry), CLAIM " +
                     "(what you are working on now; post it before editing, naming the TASK id if it is one), " +
                     "PATCH_SUMMARY (what you merged, as \"files=... | idea=... | evidence=...\", naming the TASK id " +
                     "it completes), FINDING (a problem a specialist found in merged work, as \"severity=high|medium|" +
                     "low kind=... evidence=... | what is wrong\" - severity and evidence are required), TASK (a " +
                     "backlog item - add these to split a big piece for peers). Text is at most 100 characters (300 " +
                     "for PATCH_SUMMARY, FINDING and TASK); put anything longer in detail. A CLAIM on a TASK that is " +
                     "[blocked], or that a peer who is still working holds, is refused.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "type": { "type": "string", "enum": ["OBSERVED", "FACT", "FAIL", "CLAIM", "PATCH_SUMMARY", "FINDING", "TASK"] },
                "text": { "type": "string", "description": "The gist - what a peer needs to decide whether this matters to them. At most 100 characters, or 300 for PATCH_SUMMARY, FINDING and TASK." },
                "detail": { "type": "string", "description": "Optional longer explanation, shown only when a peer calls board_unfold on this entry." },
                "after": { "type": "array", "items": { "type": "string" }, "description": "TASK only, optional: ids of TASKs already on the board (e.g. \"G4\") that must be merged before this one can be started. It is shown as [blocked] until then." },
                "lane": { "type": "string", "description": "Optional: your role's lane (b, mut, adv, lint, gap, next) when the entry is only for that lane. Leave it out for something the whole team should read." }
              },
              "required": ["type", "text"]
            }
            """)!);

    public static readonly ToolDefinition BoardGrep = new(
        Name: BoardGrepToolName,
        Description: "Search every board entry ever written, not just the latest. Keywords are case-insensitive; " +
                     "',' separates alternatives and '&' joins terms that must all match, so \"parser&fail, " +
                     "tokenizer\" finds (parser AND fail) OR tokenizer.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "query": { "type": "string" } },
              "required": ["query"]
            }
            """)!);

    public static readonly ToolDefinition BoardUnfold = new(
        Name: BoardUnfoldToolName,
        Description: "Show the full detail behind a board entry, by its id (e.g. G12).",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "id": { "type": "string" } },
              "required": ["id"]
            }
            """)!);

    public static readonly ToolDefinition Post = new(
        Name: PostToolName,
        Description: "Announce something to the whole team - an interface you are introducing, a convention, a " +
                     "change that affects others. Peers see it at the start of their next cycle, so it does not " +
                     "interrupt them. Use team_dm instead when one peer needs to know now.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "text": { "type": "string" } },
              "required": ["text"]
            }
            """)!);

    public static readonly ToolDefinition DirectMessage = new(
        Name: DirectMessageToolName,
        Description: "Send one peer a message they receive right after their current step - use it when the two " +
                     "of you are about to edit the same thing, or their work depends on something you just " +
                     "learned. It interrupts them, so use team_post for anything less urgent.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "to": { "type": "string", "description": "The peer's name, e.g. w2." },
                "text": { "type": "string" }
              },
              "required": ["to", "text"]
            }
            """)!);

    public static readonly ToolDefinition Commit = new(
        Name: CommitToolName,
        Description: "Commit everything changed in your worktree to your own branch. Commit before team_sync and " +
                     "team_merge - both work on commits, not on uncommitted edits.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "message": { "type": "string", "description": "A one-line commit message." } },
              "required": ["message"]
            }
            """)!);

    public static readonly ToolDefinition Sync = new(
        Name: SyncToolName,
        Description: "Merge the team branch - everything peers have merged so far - into your branch. If it " +
                     "reports conflicts, the conflicted files contain git's conflict markers: resolve them with " +
                     "edit_file, rebuild, and team_commit.",
        ParametersSchema: JsonNode.Parse("""{ "type": "object", "properties": {} }""")!);

    public static readonly ToolDefinition Merge = new(
        Name: MergeToolName,
        Description: "Merge your branch into the team branch. Refused unless everything is committed, you have " +
                     "synced with the latest team branch, and your current commit builds - it runs the build " +
                     "itself if you have not already built this exact commit. If a peer merged first it tells " +
                     "you to team_sync and try again.",
        ParametersSchema: JsonNode.Parse("""{ "type": "object", "properties": {} }""")!);

    public static readonly ToolDefinition Done = new(
        Name: DoneToolName,
        Description: "Tell the team you are finished: the goal is met as far as you can tell and nothing useful is " +
                     "left for you. Merge your work first - anything unmerged is lost. Releases your claim. The run " +
                     "ends early once every worker is done.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "summary": { "type": "string", "description": "One line on what you did, or why there was nothing left." } },
              "required": ["summary"]
            }
            """)!);

    /// <summary>Only offered in auto mode, where the team starts small and grows when its workers
    /// judge that the work left will not fit the time left. The runner decides; this only asks.</summary>
    public static readonly ToolDefinition RequestHelp = new(
        Name: RequestHelpToolName,
        Description: "Ask for more workers to join the team. Use it only when the open work on the board - unclaimed " +
                     "TASKs, plan steps not done - clearly cannot be finished in the time left by the workers there " +
                     "are. Put that work on the board as TASKs first, so newcomers can claim it. Newcomers start from " +
                     "the team branch as it is and read the board. The runner may refuse: the team has a size limit, " +
                     "growth is spaced out, and nobody joins when too little time or budget is left.",
        ParametersSchema: JsonNode.Parse($$"""
            {
              "type": "object",
              "properties": {
                "count": { "type": "integer", "minimum": 1, "maximum": {{MaxWorkersPerHelpRequest}}, "description": "How many more workers (1-{{MaxWorkersPerHelpRequest}})." },
                "reason": { "type": "string", "description": "Which work they should take, and why the team as it is cannot finish it in time." }
              },
              "required": ["count", "reason"]
            }
            """)!);

    /// <summary>Waiting without spending: a worker whose own work is merged and who has nothing it can take
    /// (every TASK left is blocked or held by a peer) would otherwise start cycle after cycle just to read
    /// the board again, each one paid for in tokens. The handler is the run's, which knows when the team
    /// changes; this only asks.</summary>
    public static readonly ToolDefinition Wait = new(
        Name: WaitToolName,
        Description: "Wait for the team to change, spending nothing while you do. Returns when a peer merges, posts a FAIL or " +
                     "a TASK, or messages you, or after the minutes you give. Use it when your own work is merged and every " +
                     "TASK left is [blocked] or held by a peer who is working on it - instead of reading the board again or " +
                     "inventing work. Don't use it while there is something you can do.",
        ParametersSchema: JsonNode.Parse($$"""
            {
              "type": "object",
              "properties": {
                "minutes": { "type": "integer", "minimum": 1, "maximum": {{MaxWaitMinutes}}, "description": "The longest to wait, in minutes (default {{DefaultWaitMinutes}})." }
              }
            }
            """)!);

    /// <summary>A specialist's wake-up. The runner fills a queue on every merge that lands; this takes
    /// the oldest item waiting for the caller's lane and hands back the diff and the PATCH_SUMMARY that
    /// came with it. With nothing queued it waits, spending nothing, the way team_wait does.</summary>
    public static readonly ToolDefinition NextReview = new(
        Name: NextReviewToolName,
        Description: "Take the oldest merge waiting for your lane. The call syncs your worktree to the team branch " +
                     "and returns the diff stat and the PATCH_SUMMARY to review. If nothing is queued for your lane " +
                     "it waits for the next merge, spending nothing. Review one item per cycle.",
        ParametersSchema: JsonNode.Parse("""{ "type": "object", "properties": {} }""")!);

    /// <summary>The specialist lanes' way onto the board. Not a free-text post: the severity, the kind
    /// and the evidence are what turn a finding into a TASK or into a report line, so they are fields
    /// rather than a format a model has to remember.</summary>
    public static readonly ToolDefinition ReportFinding = new(
        Name: ReportFindingToolName,
        Description: "Report a problem with merged work. severity=high only with evidence of a failing test or a " +
                     "reproducer; anything else is medium or low. fix_by says who should act: builders (means it " +
                     "becomes a TASK when it is high), me (your lane fixes it), or user (out of scope - it is shown " +
                     "to the user at the end). The finding is posted to the board for you.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "severity": { "type": "string", "enum": ["high", "medium", "low"], "description": "high needs evidence: a failing test or a concrete reproducer." },
                "kind": { "type": "string", "description": "What sort of problem: mutation, security, robustness, style, gap, followup, ..." },
                "text": { "type": "string", "description": "The finding, in one or two sentences." },
                "files": { "type": "array", "items": { "type": "string" }, "description": "The files it is about." },
                "evidence": { "type": "string", "description": "How you know: a test name, a reproducer, or branch:<sha> for a failing test committed in your own branch." },
                "fix_by": { "type": "string", "enum": ["builders", "me", "user"], "description": "Who should act on it. Default builders." }
              },
              "required": ["severity", "kind", "text"]
            }
            """)!);

    /// <summary>The mutation tester's lane. Each mutant is a write, a build and a test run, so the
    /// caller says how many it can afford; the original file is always restored.</summary>
    public static readonly ToolDefinition RunMutationTests = new(
        Name: RunMutationTestsToolName,
        Description: "Mutate the changed lines of a file and see whether the tests notice. Operators: relational and " +
                     "equality flips, an off-by-one boundary, a negated condition, an &&/|| swap, a statement-level " +
                     "return of default, and a removed void call. Each surviving mutant is a gap in the tests. The " +
                     "file is restored afterwards. Costs one build and one test run per mutant.",
        ParametersSchema: JsonNode.Parse($$"""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "The file to mutate, relative to the solution directory." },
                "max_mutants": { "type": "integer", "minimum": 1, "maximum": {{MaxMutantsPerRun}}, "description": "How many mutants to try (default {{DefaultMutantsPerRun}})." },
                "test_filter": { "type": "string", "description": "Optional test filter, the same value run_tests takes." }
              },
              "required": ["file"]
            }
            """)!);

    /// <summary>The conformity lane's check: style and analyzers in verify mode over the merged files,
    /// plus the build's warnings for those files. Run through the SDK directly, not run_command, so the
    /// user is never asked to approve the team's own lint.</summary>
    public static readonly ToolDefinition CheckConformity = new(
        Name: CheckConformityToolName,
        Description: "Check the style of merged files: dotnet format style and dotnet format analyzers in verify " +
                     "mode over exactly these files, then a build keeping the warnings those files raise. Returns " +
                     "what is wrong and where. Style-only problems are yours to fix; anything that would change " +
                     "behaviour is a FINDING instead.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "files": { "type": "array", "items": { "type": "string" }, "description": "The files to check, relative to the solution directory." }
              },
              "required": ["files"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> Coordination { get; } =
        [BoardRead, BoardWrite, BoardGrep, BoardUnfold, Post, DirectMessage, Commit, Sync, Merge, Done];

    /// <summary>The existing tools a worker also gets, run against its worktree rather than the
    /// user's checkout. See the type doc for why this is an allow-list.</summary>
    public static IReadOnlyList<ToolDefinition> AllowedExistingTools { get; } =
    [
        WorkspaceTools.ReadFile,
        WorkspaceTools.ListFiles,
        WorkspaceTools.SearchCodebase,
        SymbolTools.FindSymbol,
        SymbolTools.FindReferences,
        DiagnosticTools.GetDiagnostics,
        GitTools.Diff,
        GitTools.Status,
        BuildTools.Build,
        TestTools.RunTests,
    ];

    /// <summary>
    /// Everything a worker is offered, by role. edit_file's definition comes from the caller because
    /// it shares propose_edit's schema, which lives in IdePlusPlus.Copilot.Diff.
    ///
    /// A role that may not edit is not offered edit_file at all, and one with no branch to merge is
    /// not offered team_merge - a tool the prompt never explains is a tool the model still reaches
    /// for, and refusing it afterwards costs a step. The dispatcher refuses both as well, since what
    /// is hidden is not what is enforced.
    /// </summary>
    /// <param name="role">The worker's role. Null means a builder - the roster Ultra ran with before
    /// roles existed, which keeps every existing caller and test working unchanged.</param>
    public static IReadOnlyList<ToolDefinition> ForWorker(
        ToolDefinition editFile, bool shellCommandsEnabled, bool canRequestHelp = false, bool canWait = false,
        TeamRole? role = null)
    {
        var effective = role ?? TeamRoles.Builder;
        return
        [
            .. AllowedExistingTools,
            .. effective.CanEdit ? new[] { editFile } : [],
            .. shellCommandsEnabled ? ShellTools.All : [],
            .. CoordinationFor(effective),
            .. canRequestHelp ? [RequestHelp] : Array.Empty<ToolDefinition>(),
            .. canWait ? [Wait] : Array.Empty<ToolDefinition>(),
        ];
    }

    /// <summary>The team tools a role is offered: the ordinary set, minus team_merge where the role
    /// has nothing to merge, plus the specialist lane's own.</summary>
    public static IReadOnlyList<ToolDefinition> CoordinationFor(TeamRole? role)
    {
        var effective = role ?? TeamRoles.Builder;
        return
        [
            .. effective.CanMerge ? Coordination : Coordination.Where(t => t.Name != MergeToolName),
            .. effective.IsSpecialist ? new ToolDefinition[] { NextReview, ReportFinding } : Array.Empty<ToolDefinition>(),
            .. effective.Kind == TeamRoleKind.MutationTester ? new ToolDefinition[] { RunMutationTests } : Array.Empty<ToolDefinition>(),
            .. effective.Kind == TeamRoleKind.Conformity ? new ToolDefinition[] { CheckConformity } : Array.Empty<ToolDefinition>(),
        ];
    }

    public static bool IsCoordinationTool(string toolName) =>
        toolName is RequestHelpToolName or WaitToolName or NextReviewToolName or ReportFindingToolName
            or RunMutationTestsToolName or CheckConformityToolName
        || Coordination.Any(t => t.Name == toolName);
}
