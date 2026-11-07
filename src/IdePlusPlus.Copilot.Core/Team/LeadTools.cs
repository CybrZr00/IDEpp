using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team;

/// <summary>
/// What the lead can call when an Ultra run is orchestrated. The lead never edits code and has no
/// worktree: it plans, supervises and reviews. Its reading tools are the same read-only set a
/// sub-agent gets, and everything it does to the team goes through these four.
/// </summary>
public static class LeadTools
{
    public const string SubmitPlanToolName = "lead_submit_plan";
    public const string VerdictToolName = "lead_verdict";
    public const string MessageToolName = "lead_message";
    public const string AddTaskToolName = "lead_add_task";

    /// <summary>The lead's name on the message bus and the board.</summary>
    public const string LeadName = "lead";

    public const int MaxPlanTasks = 30;

    public static readonly ToolDefinition SubmitPlan = new(
        Name: SubmitPlanToolName,
        Description: "Submit the plan: the independent pieces of work the team will do, as short task descriptions. " +
                     "Each becomes a TASK on the board for a worker to claim. Make them as independent as you can - " +
                     "name the files or area each one owns - so workers rarely edit the same thing. Call this once, " +
                     "when you have read enough to split the goal sensibly.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "tasks": {
                  "type": "array",
                  "items": { "type": "string", "description": "One piece of work, at most about 280 characters: the outcome, and the files or area it touches." }
                },
                "notes": { "type": "string", "description": "Optional: conventions or interfaces every worker should follow. Posted to the team." }
              },
              "required": ["tasks"]
            }
            """)!);

    public static readonly ToolDefinition Verdict = new(
        Name: VerdictToolName,
        Description: "Give your decision on the merge you were asked to review. Approve unless the change is wrong, " +
                     "outside the goal, or conflicts with the plan; when you reject, say exactly what to change.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "approve": { "type": "boolean" },
                "feedback": { "type": "string", "description": "Required when rejecting: what is wrong and what to do instead." }
              },
              "required": ["approve"]
            }
            """)!);

    public static readonly ToolDefinition Message = new(
        Name: MessageToolName,
        Description: "Tell one worker, or every worker, something. Use it to redirect a worker that has drifted, " +
                     "split two workers who are on the same thing, or answer a question. It reaches them right after " +
                     "their current step.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "to": { "type": "string", "description": "A worker's name, e.g. w2, or \"all\"." },
                "text": { "type": "string" }
              },
              "required": ["to", "text"]
            }
            """)!);

    public static readonly ToolDefinition AddTask = new(
        Name: AddTaskToolName,
        Description: "Add one more task to the board - for work the plan missed, or a follow-up a review turned up.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": { "text": { "type": "string", "description": "At most about 280 characters." } },
              "required": ["text"]
            }
            """)!);

    /// <summary>What the lead may read while planning: the same read-only tools a sub-agent gets.</summary>
    public static IReadOnlyList<ToolDefinition> ForPlanning { get; } = [.. SubAgentTools.AllowedTools, SubmitPlan];

    /// <summary>What the lead has on a supervision check.</summary>
    public static IReadOnlyList<ToolDefinition> ForSupervision { get; } = [.. SubAgentTools.AllowedTools, Message, AddTask];

    /// <summary>What the lead has when reviewing one merge.</summary>
    public static IReadOnlyList<ToolDefinition> ForReview { get; } = [.. SubAgentTools.AllowedTools, Verdict];
}
