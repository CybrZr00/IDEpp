using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Diff;

public static class ProposeEditTool
{
    public const string ToolName = "propose_edit";

    public static readonly ToolDefinition Definition = new(
        Name: ToolName,
        Description: "Apply an edit to a file in the user's project. Call this to actually make a change - describing " +
                     "the change in chat text does nothing, and the user cannot act on a described edit. Prefer " +
                     "mode 'replace_range' with the line numbers shown by read_file: it only sends the lines you are " +
                     "changing, so it is far cheaper and less error-prone than restating a whole file. Reserve " +
                     "'whole_file' for new files or near-total rewrites. Call it once per file; several files means " +
                     "several calls, which you can make in the same turn.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "Path to the file, relative to the open solution's directory. An absolute path is accepted only when it resolves inside that directory - anything outside the open solution is refused." },
                "mode": { "type": "string", "enum": ["whole_file", "replace_range"], "description": "replace_range replaces only start_line..end_line and is preferred for anything short of a full rewrite; whole_file replaces the entire file." },
                "start_line": { "type": "integer", "description": "1-based first line to replace, as shown by read_file. Required when mode is replace_range." },
                "end_line": { "type": "integer", "description": "1-based last line to replace, inclusive, as shown by read_file. Required when mode is replace_range. Use the same value as start_line to replace a single line; to insert without deleting, replace the line you are inserting after and include it in new_text." },
                "new_text": { "type": "string", "description": "Replacement content: the whole file (whole_file) or just the replaced lines (replace_range). Plain file content only - no line-number prefixes from read_file, and no markdown code fences." }
              },
              "required": ["file", "mode", "new_text"]
            }
            """)!);
}
