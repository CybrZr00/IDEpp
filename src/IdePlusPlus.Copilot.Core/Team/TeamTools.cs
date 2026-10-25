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
    public const string EditFileToolName = "edit_file";

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
                     "it completes), TASK (a backlog item - add these to split a big piece for peers). Text is at " +
                     "most 100 characters (300 for PATCH_SUMMARY and TASK); put anything longer in detail.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "type": { "type": "string", "enum": ["OBSERVED", "FACT", "FAIL", "CLAIM", "PATCH_SUMMARY", "TASK"] },
                "text": { "type": "string", "description": "The gist - what a peer needs to decide whether this matters to them. At most 100 characters, or 300 for PATCH_SUMMARY." },
                "detail": { "type": "string", "description": "Optional longer explanation, shown only when a peer calls board_unfold on this entry." }
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

    /// <summary>Everything a worker is offered. edit_file's definition comes from the caller because
    /// it shares propose_edit's schema, which lives in IdePlusPlus.Copilot.Diff.</summary>
    public static IReadOnlyList<ToolDefinition> ForWorker(ToolDefinition editFile, bool shellCommandsEnabled) =>
    [
        .. AllowedExistingTools,
        editFile,
        .. shellCommandsEnabled ? ShellTools.All : [],
        .. Coordination,
    ];

    public static bool IsCoordinationTool(string toolName) => Coordination.Any(t => t.Name == toolName);
}
