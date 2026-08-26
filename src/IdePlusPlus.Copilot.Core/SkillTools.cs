using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Reads one saved skill's full instructions by name. Definitions only - the handler lives in the
/// chat pane alongside SkillStore.
///
/// Every turn's context already lists each saved skill's name and description (see
/// CopilotChatPaneViewModel.PrependContextAsync) - full bodies aren't injected unconditionally,
/// since that stops scaling the moment there's more than a couple of them. This is how the model
/// actually loads one, once it has decided a particular one is relevant.
/// </summary>
public static class SkillTools
{
    public const string ReadSkillToolName = "read_skill";

    public static bool IsSkillTool(string toolName) => toolName is ReadSkillToolName;

    public static readonly ToolDefinition ReadSkill = new(
        Name: ReadSkillToolName,
        Description: "Read the full instructions for one saved skill, by the name shown in the skill catalog " +
                     "at the top of this conversation. Call this before following a skill - the catalog only " +
                     "carries its one-line description.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The skill's name, exactly as shown in the catalog." }
              },
              "required": ["name"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [ReadSkill];
}
