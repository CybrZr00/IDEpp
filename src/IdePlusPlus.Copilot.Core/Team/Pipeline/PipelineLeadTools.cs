using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// What the orchestrator ("lead") can call in an Ultra Pipeline run. It decides three things and nothing
/// else - who is on the roster, what becomes of the findings that need judgement, and who is right when
/// an appeal comes to it - each by one submit tool. It reads code with the same read-only tools a
/// surveyor has, edits nothing and has no worktree.
///
/// These sit beside <see cref="LeadTools"/>, the original team's four, rather than in it: that lead plans
/// a shared backlog and supervises workers, which the pipeline does not have.
/// </summary>
public static class PipelineLeadTools
{
    public const string SubmitRosterToolName = "lead_submit_roster";
    public const string SubmitTriageToolName = "lead_submit_triage";
    public const string ArbitrateToolName = "lead_arbitrate";

    public static readonly ToolDefinition SubmitRoster = new(
        Name: SubmitRosterToolName,
        Description: "Submit the roster: the surveyors that read the code before anything is designed, and any judges " +
                     "of your own besides the standard four. Call this once, when you have read enough to see what the " +
                     "goal touches. An empty roster is a valid decision - a small goal needs no surveyors, and the " +
                     "designer then surveys for itself.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "surveyors": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "area": { "type": "string", "description": "The part of the code to survey: a folder, a project or a feature. Each surveyor's area is its own." },
                      "question": { "type": "string", "description": "Optional: the one question that matters most there." }
                    },
                    "required": ["area"]
                  }
                },
                "customJudges": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "title": { "type": "string", "description": "What the judge is called, e.g. \"Accessibility judge\"." },
                      "charter": { "type": "string", "description": "What it checks and what a failure looks like, in under 800 characters." },
                      "scope": { "type": "string", "enum": ["none", "testsOnly"], "description": "none: reads and reports. testsOnly: may also write failing tests. A judge never edits the code it judges." },
                      "focusFiles": { "type": "array", "items": { "type": "string" }, "description": "Optional: files, relative to the solution folder, it should look at first." }
                    },
                    "required": ["title", "charter"]
                  }
                },
                "rationale": { "type": "string", "description": "Optional: one or two sentences on why this roster." }
              }
            }
            """)!);

    public static readonly ToolDefinition SubmitTriage = new(
        Name: SubmitTriageToolName,
        Description: "Submit your decision for every finding you were given. assign: an owner repairs it - name the owner and " +
                     "say exactly what to change. dismiss: it is not a real problem, or not worth what fixing it costs. defer: " +
                     "it is real but outside this goal; give a request the user could run later. A finding that spans two " +
                     "owners' files is assigned to each of them, with that owner's part. Call this once, with every finding " +
                     "decided.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "decisions": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "finding": { "type": "string", "description": "The finding's id, e.g. \"F1.2\"." },
                      "action": { "type": "string", "enum": ["assign", "dismiss", "defer"] },
                      "owner": { "type": "string", "description": "For assign: the owner who repairs it, e.g. \"b2\". It must own one of the finding's files." },
                      "instruction": { "type": "string", "description": "For assign: exactly what the owner is to change." },
                      "reason": { "type": "string", "description": "For dismiss and defer: why, in at least 20 characters." },
                      "nextPrompt": { "type": "string", "description": "For defer: a request the user could run later to do this work." }
                    },
                    "required": ["finding", "action"]
                  }
                },
                "notes": { "type": "string", "description": "Optional: anything the next round of judging, or the user, should know." }
              },
              "required": ["decisions"]
            }
            """)!);

    public static readonly ToolDefinition Arbitrate = new(
        Name: ArbitrateToolName,
        Description: "Rule on the appeal you were asked about. Your decision is final. requester: the owner must make the " +
                     "change. owner: the refusal stands and the requester carries on without it. Either way, say in " +
                     "'instruction' what whoever has to act is to do.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "request": { "type": "string", "description": "The change request's id, e.g. \"CR3\"." },
                "ruling": { "type": "string", "enum": ["requester", "owner"] },
                "instruction": { "type": "string", "description": "For requester: exactly what the owner is to change. For owner: what the requester does instead." }
              },
              "required": ["request", "ruling", "instruction"]
            }
            """)!);

    /// <summary>What the orchestrator has while it decides the roster: the surveyors' reading tools and the submit tool.</summary>
    public static IReadOnlyList<ToolDefinition> ForRoster { get; } = [.. SubAgentTools.AllowedTools, SubmitRoster];

    public static IReadOnlyList<ToolDefinition> ForTriage { get; } = [.. SubAgentTools.AllowedTools, SubmitTriage];

    public static IReadOnlyList<ToolDefinition> ForArbitration { get; } = [.. SubAgentTools.AllowedTools, Arbitrate];
}
