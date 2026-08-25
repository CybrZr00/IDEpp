using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Read-only tools that let the model actually investigate the open solution on demand.
///
/// Before these existed, the only callable tools were propose_edit/propose_plan plus whatever MCP
/// servers the user had configured - file context reached the model exclusively as pre-injected
/// system messages (the open file, and top-K embedding search hits). That gap made Plan Mode's
/// "investigate first" instruction impossible to satisfy: a capable model would obediently reach
/// for the only exploration-shaped tools it had (e.g. Playwright browser automation) and loop until
/// the tool-iteration cap. These tools close that gap.
/// </summary>
public static class WorkspaceTools
{
    public const string ReadFileToolName = "read_file";
    public const string ListFilesToolName = "list_files";
    public const string SearchCodebaseToolName = "search_codebase";

    public static bool IsWorkspaceTool(string toolName) =>
        toolName is ReadFileToolName or ListFilesToolName or SearchCodebaseToolName;

    public static readonly ToolDefinition ReadFile = new(
        Name: ReadFileToolName,
        Description: "Read a file from the open solution. Use this to inspect real code before answering or planning - " +
                     "never guess at a file's contents. Each line is returned prefixed with its 1-based line number " +
                     "followed by ': '. Those prefixes are NOT part of the file - use them for propose_edit's " +
                     "start_line/end_line, and never copy them into propose_edit's new_text. A file you have already " +
                     "read this conversation does not need re-reading unless you have since changed it - but "
                     + "if you have changed it, do re-read, because the line numbers below your edit have moved. "
                     + "Long files come back a page at a time; the reply says how to read the rest.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "file": { "type": "string", "description": "Path relative to the solution directory, e.g. src/Foo/Bar.cs" },
                "offset": { "type": "integer", "description": "1-based line to start from. Use this to continue reading a file whose reply said it was truncated. Omit to start at the beginning." },
                "limit": { "type": "integer", "description": "Maximum number of lines to return. Omit to get as many as fit." }
              },
              "required": ["file"]
            }
            """)!);

    public static readonly ToolDefinition ListFiles = new(
        Name: ListFilesToolName,
        Description: "List files and folders in the open solution, to discover what exists before reading specific files. " +
                     "Build output (bin/obj), .git and .vs are excluded.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "directory": { "type": "string", "description": "Optional path relative to the solution directory. Omit for the solution root." },
                "recursive": { "type": "boolean", "description": "List nested contents too. Defaults to false." }
              }
            }
            """)!);

    public static readonly ToolDefinition SearchCodebase = new(
        Name: SearchCodebaseToolName,
        Description: "Semantic search over the indexed codebase, returning the most relevant code chunks. " +
                     "Use when you don't yet know which file to read. Requires the codebase to have been indexed.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "What to search for, in natural language." }
              },
              "required": ["query"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [ReadFile, ListFiles, SearchCodebase];
}
