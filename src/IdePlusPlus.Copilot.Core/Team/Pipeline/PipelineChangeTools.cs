using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

// What each tool takes, as the model sends it. Everything is text or a number the model may have written as text.
public sealed record RequestChangeArguments(string? File, string? Change, string? Rationale);

public sealed record AwaitChangeArguments(string? Id, int? Minutes);

public sealed record RespondChangeArguments(string? Id, string? Decision, string? Reasoning);

public sealed record ChangeDoneArguments(string? Id, string? Summary);

public sealed record AppealChangeArguments(string? Id, string? Argument);

public sealed record ClaimNewFileArguments(string? Path, string? Reason);

/// <summary>
/// The owners' way of getting a change into a file that is not theirs. Every file has one owner and only that
/// owner edits it, so an owner that needs something from another file asks - <c>request_change</c> opens a request
/// and returns at once, <c>await_change</c> waits for the answer - and the owner it asks answers
/// (<c>respond_change</c>), makes the change and reports it (<c>change_done</c>). A rejection can be appealed once
/// (<c>appeal_change</c>) and the orchestrator decides. A file nobody owns and that does not exist yet can be
/// claimed (<c>claim_new_file</c>) rather than asked for. The state of every request is the
/// <see cref="ChangeRequestRegistry"/>'s; these are only the tools that reach it.
/// </summary>
public static class PipelineChangeTools
{
    public const string RequestChangeToolName = "request_change";
    public const string AwaitChangeToolName = "await_change";
    public const string RespondChangeToolName = "respond_change";
    public const string ChangeDoneToolName = "change_done";
    public const string AppealChangeToolName = "appeal_change";
    public const string ClaimNewFileToolName = "claim_new_file";

    /// <summary>The longest one await_change waits, in the unit the run uses (a minute).</summary>
    public const int MaxAwaitMinutes = 10;

    public const int DefaultAwaitMinutes = 5;

    /// <summary>The shortest reason a claim for a new file needs.</summary>
    public const int MinClaimReasonLength = 10;

    public static readonly ToolDefinition RequestChange = new(
        Name: RequestChangeToolName,
        Description: "Ask the owner of a file you do not own to change it. Say what should change and why - the owner decides on " +
                     "the reasons. This returns at once with an id (CR1, CR2, ...); carry on with your own files, and call " +
                     "await_change when you need the answer. You may have a few requests open at once.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "The file, relative to the solution folder." },
                "change": { "type": "string", "description": "Exactly what should change." },
                "rationale": { "type": "string", "description": "Why your work needs it." }
              },
              "required": ["file", "change", "rationale"]
            }
            """)!);

    public static readonly ToolDefinition AwaitChange = new(
        Name: AwaitChangeToolName,
        Description: "Wait for a request you made to change: the owner answers, makes the change, or the request times out. Costs " +
                     "nothing while it waits. It returns when anything changes, so call it again for the next step - an " +
                     "accepted request is not done until the owner says so. Done means team_sync to bring the change in.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "id": { "type": "string", "description": "The request's id, e.g. \"CR1\"." },
                "minutes": { "type": "integer", "description": "How long to wait, 1 to 10. Default 5." }
              },
              "required": ["id"]
            }
            """)!);

    public static readonly ToolDefinition RespondChange = new(
        Name: RespondChangeToolName,
        Description: "Answer a request to change one of your files. Accept it and you will make the change - then build, " +
                     "team_commit, team_merge, and call change_done. Reject it with reasons the requester can act on (at least " +
                     "20 characters): what is wrong with the request, or what to do instead. A rejection is a legitimate answer; " +
                     "the requester may appeal it to the lead, once.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "decision": { "type": "string", "enum": ["accept", "reject"] },
                "reasoning": { "type": "string", "description": "Required for a rejection; optional note for an acceptance." }
              },
              "required": ["id", "decision"]
            }
            """)!);

    public static readonly ToolDefinition ChangeDone = new(
        Name: ChangeDoneToolName,
        Description: "Report that a change you accepted is made and merged to the team branch. It must be on the team branch - " +
                     "the requester brings it into its own worktree with team_sync.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "summary": { "type": "string", "description": "What you changed." }
              },
              "required": ["id"]
            }
            """)!);

    public static readonly ToolDefinition AppealChange = new(
        Name: AppealChangeToolName,
        Description: "Contest the rejection of a request you made - once. Say why the owner's reasons do not hold. The lead decides, " +
                     "and the decision is final: either the owner must make the change, or the refusal stands and you carry on " +
                     "without it. This waits for the lead's decision.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "id": { "type": "string" },
                "argument": { "type": "string", "description": "Why the owner's reasons do not hold (at least 20 characters)." }
              },
              "required": ["id", "argument"]
            }
            """)!);

    public static readonly ToolDefinition ClaimNewFile = new(
        Name: ClaimNewFileToolName,
        Description: "Take ownership of a file the design did not list so that you can create it. Only for a path nobody owns that does " +
                     "not exist yet and that does not define how the solution builds (no project, solution or props files). Anything " +
                     "else is a request to its owner, or something to say in your report.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "path": { "type": "string", "description": "Relative to the solution folder, with forward slashes." },
                "reason": { "type": "string", "description": "Why your work needs this file." }
              },
              "required": ["path", "reason"]
            }
            """)!);

    /// <summary>All six, in the order an owner uses them.</summary>
    public static IReadOnlyList<ToolDefinition> All { get; } =
        [RequestChange, AwaitChange, RespondChange, ChangeDone, AppealChange, ClaimNewFile];

    public static bool IsChangeTool(string toolName) => All.Any(t => t.Name == toolName);
}
