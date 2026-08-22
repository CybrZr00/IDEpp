using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

public static class ProposePlanTool
{
    public const string ToolName = "propose_plan";

    public static readonly ToolDefinition Definition = new(
        Name: ToolName,
        Description: "Present an implementation plan to the user for approval. Call this once you've " +
                     "investigated (read files, searched, used read-only tools) and know what you'd change - " +
                     "do not edit any files yet. The user will approve or reject the plan before you're given " +
                     "the ability to make changes.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "summary": { "type": "string", "description": "One or two sentences describing the overall approach." },
                "steps": { "type": "array", "items": { "type": "string" }, "description": "Ordered, concrete implementation steps." }
              },
              "required": ["summary", "steps"]
            }
            """)!);
}
