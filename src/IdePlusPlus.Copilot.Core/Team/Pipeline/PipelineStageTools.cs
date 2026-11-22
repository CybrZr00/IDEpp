using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// The tool each stage's agents hand their result in with - one per kind of artifact. An agent's result
/// is only ever a call to its submit tool: the runner checks it, writes the artifact, and nothing the
/// agent says in the conversation is kept. (The orchestrator's own are <see cref="PipelineLeadTools"/>.)
/// </summary>
public static class PipelineStageTools
{
    public const string SurveySubmitToolName = "survey_submit";

    public static readonly ToolDefinition SurveySubmit = new(
        Name: SurveySubmitToolName,
        Description: "Hand in your survey. Call this once, when you have read enough of your area. List what already exists " +
                     "that the goal can reuse (each with the file that shows it), what does not exist yet but the goal needs " +
                     "(each with why), and what looks dead, duplicated or over-built (each with the evidence: who calls it, " +
                     "how many references). Every item names a file. A trim is a recommendation and nothing more.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "area": { "type": "string", "description": "Optional: the area you surveyed. Defaults to the one you were given." },
                "summary": { "type": "string", "description": "One or two sentences: what you found, overall." },
                "exists": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "pattern": { "type": "string", "description": "A pattern, helper or convention the goal can reuse." },
                      "file": { "type": "string", "description": "The file that shows it, relative to the solution folder." },
                      "note": { "type": "string", "description": "Optional: how to use it, or a caveat." }
                    },
                    "required": ["pattern", "file"]
                  }
                },
                "needs": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "what": { "type": "string", "description": "What does not exist yet that the goal needs." },
                      "rationale": { "type": "string", "description": "Why the goal needs it." },
                      "file": { "type": "string", "description": "Optional: where it would go or what it would touch." }
                    },
                    "required": ["what", "rationale"]
                  }
                },
                "trims": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "what": { "type": "string", "description": "What looks dead, duplicated or over-built." },
                      "file": { "type": "string", "description": "Where it is, relative to the solution folder." },
                      "evidence": { "type": "string", "description": "Who calls it, how many references - what you checked." }
                    },
                    "required": ["what", "file", "evidence"]
                  }
                }
              }
            }
            """)!);

    /// <summary>What a surveyor has: the read-only tools every sub-agent has, and its submit tool. Nothing
    /// that writes, and no commands.</summary>
    public static IReadOnlyList<ToolDefinition> ForSurveyor { get; } = [.. SubAgentTools.AllowedTools, SurveySubmit];
}
