using System.Globalization;
using System.Text;
using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Turns a search result into the text the model reads. It says how the search was done whenever that
/// limits what the model can conclude: keyword-only hits are not a similarity ranking, and a model
/// that does not know the difference will treat a weak one as strong.
/// </summary>
public static class TranscriptSearchFormatter
{
    public static string Format(string query, TranscriptSearchResult result, string? currentConversationId)
    {
        var notes = Notes(result);
        if (result.Hits.Count == 0)
        {
            return $"No matches in the saved conversation transcripts for \"{query}\"." + notes;
        }

        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{result.Hits.Count} match(es) in the saved conversation transcripts, closest first.");
        text.Append(notes);

        for (var i = 0; i < result.Hits.Count; i++)
        {
            var entry = result.Hits[i].Entry;
            var conversation = entry.ConversationId == currentConversationId
                ? "this conversation"
                : $"conversation {entry.ConversationId}";

            text.Append(CultureInfo.InvariantCulture, $"\n\n[{i + 1}] {entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm} | {Label(entry)} | {conversation} | entry {entry.Sequence}");
            text.Append('\n');
            text.Append(result.Hits[i].Excerpt);
        }

        return text.ToString();
    }

    private static string Label(TranscriptEntry entry) => entry.Kind switch
    {
        TranscriptKinds.User => "user message",
        TranscriptKinds.Assistant => "assistant message",
        TranscriptKinds.Thought => "assistant thought",
        var other => other,
    };

    private static string Notes(TranscriptSearchResult result)
    {
        var notes = new StringBuilder();
        if (!result.SemanticAvailable)
        {
            notes.Append(
                " Matching by meaning is not available right now (no embedding model is ready), so these are "
                + "keyword matches only - use the exact words the original text would have used.");
        }
        else if (result.UnembeddedPassages > 0)
        {
            notes.Append(CultureInfo.InvariantCulture,
                $" {result.UnembeddedPassages} stored passage(s) are not embedded yet and were matched by keyword only.");
        }

        return notes.ToString();
    }
}
