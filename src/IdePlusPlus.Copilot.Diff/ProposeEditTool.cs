using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Diff;

public static class ProposeEditTool
{
    public const string ToolName = "propose_edit";

    public static readonly ToolDefinition Definition = new(
        Name: ToolName,
        Description: "Propose an edit to a file in the user's project. Always call this when asked to create, " +
                     "modify, or fix a file - never paste the new code as plain chat text instead.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "Path to the file, relative to the open solution's directory, or absolute." },
                "mode": { "type": "string", "enum": ["whole_file", "replace_range"], "description": "whole_file replaces the entire file; replace_range replaces only start_line..end_line." },
                "start_line": { "type": "integer", "description": "1-based first line to replace. Only used when mode is replace_range." },
                "end_line": { "type": "integer", "description": "1-based last line to replace, inclusive. Only used when mode is replace_range." },
                "new_text": { "type": "string", "description": "The full replacement content for the file (whole_file) or the replaced range (replace_range)." }
              },
              "required": ["file", "mode", "new_text"]
            }
            """)!);
}
