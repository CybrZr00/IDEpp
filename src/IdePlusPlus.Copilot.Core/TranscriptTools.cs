using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Searches the saved transcripts of earlier conversations. Definitions only - the handler lives in
/// the chat pane, next to the transcript service it calls.
///
/// New Chat clears the live conversation and Save Memory keeps only a short curated summary, so
/// detail goes missing from what the model can see. The full transcripts, the model's own reasoning
/// included, are still on disk; this is how the model gets back to them when it needs a detail or a
/// decision the summary left out.
/// </summary>
public static class TranscriptTools
{
    public const string SearchTranscriptsToolName = "search_transcripts";

    public const string KindAny = "any";
    public const string KindMessage = "message";
    public const string KindThought = "thought";

    public const int DefaultLimit = 5;
    public const int MaxLimit = 10;

    public static bool IsTranscriptTool(string toolName) => toolName is SearchTranscriptsToolName;

    public static readonly ToolDefinition SearchTranscripts = new(
        Name: SearchTranscriptsToolName,
        Description: "Search the saved transcripts of this project's earlier Copilot conversations: what the user " +
                     "asked, what you answered, and your own reasoning at the time (\"thought\" entries, where the " +
                     "model produced any). New Chat clears the conversation and the project memory keeps only a " +
                     "short summary, but the full transcripts are kept - use this to recover a detail, a decision, " +
                     "or the reasoning behind one that the summary left out, or when the user refers to something " +
                     "discussed before. Do not use it for what is already in this conversation. Results are the " +
                     "closest matches, not guaranteed to be relevant: if they are weak, refine the query rather " +
                     "than relying on them. Matching is by meaning when an embedding model is available and by " +
                     "keywords otherwise, so name concrete things - file names, identifiers, error text - where " +
                     "you can.",
        ParametersSchema: JsonNode.Parse("""
            {
              "type": "object",
              "properties": {
                "query": { "type": "string", "description": "What to look for, in natural language, with any concrete names or error text it would have contained." },
                "kind": { "type": "string", "enum": ["any", "message", "thought"], "description": "'message' searches only what was said (by the user or by you), 'thought' only your earlier reasoning. Defaults to 'any'." },
                "limit": { "type": "integer", "description": "How many matches to return, 1 to 10. Defaults to 5." }
              },
              "required": ["query"]
            }
            """)!);

    public static IReadOnlyList<ToolDefinition> All => [SearchTranscripts];
}
