using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Diff;

public static class ProposeEditTool
{
    public const string ToolName = "propose_edit";

    public static readonly ToolDefinition Definition = new(
        Name: ToolName,
        Description: "Apply an edit to a file in the user's project. Call this to actually make a change - describing " +
                     "the change in chat text does nothing, and the user cannot act on a described edit. " +
                     "Prefer mode 'replace_text': you quote the exact snippet you want gone and its replacement, so " +
                     "it cannot be thrown off by line numbers that moved. Use 'replace_range' when you have line " +
                     "numbers you know are current. Reserve 'whole_file' for creating a new file. " +
                     "Call it once per file; several files means several calls, which you can make in the same turn.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "Path to the file, relative to the open solution's directory. An absolute path is accepted only when it resolves inside that directory - anything outside the open solution is refused." },
                "mode": { "type": "string", "enum": ["replace_text", "replace_range", "whole_file"], "description": "replace_text swaps one exact snippet for another and is preferred - it does not depend on line numbers. replace_range replaces start_line..end_line. whole_file replaces the entire file and is for new files only." },
                "old_text": { "type": "string", "description": "Required when mode is replace_text. The exact text to replace, copied verbatim from read_file WITHOUT the line-number prefixes. It must appear exactly once in the file: include enough surrounding lines to make it unique, and the edit is refused rather than guessed at if it matches nothing or matches more than once." },
                "start_line": { "type": "integer", "description": "1-based first line to replace, as shown by read_file. Required when mode is replace_range." },
                "end_line": { "type": "integer", "description": "1-based last line to replace, inclusive, as shown by read_file. Required when mode is replace_range - it is not optional and there is no default. Use the same value as start_line to replace a single line. Note that line numbers shift after every edit, so re-read a file you have already changed before using them." },
                "new_text": { "type": "string", "description": "Replacement content: the text that takes old_text's place (replace_text), just the replaced lines (replace_range), or the whole file (whole_file). Plain file content only - no line-number prefixes from read_file, and no markdown code fences. Never abbreviate with a placeholder like '... rest of file unchanged ...'; whatever you send is written to disk exactly as given." }
              },
              "required": ["file", "mode", "new_text"]
            }
            """)!);
}
