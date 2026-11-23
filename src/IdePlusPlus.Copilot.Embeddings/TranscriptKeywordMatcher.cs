namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// The keyword half of transcript search. It exists because the embedding half is not always there:
/// the built-in model is only downloaded when the codebase is first indexed, and an LM Studio server
/// may not be running. It also catches what embeddings are bad at - an exact identifier or an error
/// message quoted from a build - so it runs alongside them rather than only as a fallback.
/// </summary>
public static class TranscriptKeywordMatcher
{
    /// <summary>Words too common in a question to say anything about which passage answers it.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "that", "this", "with", "what", "was", "were", "how", "did", "you", "are", "not",
        "have", "has", "had", "from", "about", "can", "why", "when", "which", "who", "does", "our", "your",
        "its", "into", "than", "then", "there", "they", "them", "but", "all", "any", "out", "over", "just",
        "of", "to", "in", "is", "it", "on", "at", "be", "as", "or", "an", "by", "if", "so", "do", "we", "my",
        "me", "up", "no", "he", "us", "am", "go",
    };

    /// <summary>Terms shorter than this are matched as whole words only: "db" should find "the db is
    /// down" but not "feedback". Longer terms match inside words, so "PersistChat" finds
    /// "PersistChatHistory".</summary>
    private const int WholeWordBelowLength = 4;

    /// <summary>The distinct search terms in a question: runs of letters, digits and underscores, two
    /// characters or longer, minus stop words. "PersistChatHistory" stays one term, and "40" and "db"
    /// are terms, since a conversation about code is full of them.</summary>
    public static IReadOnlyList<string> Terms(string query)
    {
        var terms = new List<string>();
        var start = -1;
        for (var i = 0; i <= query.Length; i++)
        {
            var isWordChar = i < query.Length && (char.IsLetterOrDigit(query[i]) || query[i] == '_');
            if (isWordChar)
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                var word = query[start..i];
                if (word.Length >= 2 && !StopWords.Contains(word) && !terms.Contains(word, StringComparer.OrdinalIgnoreCase))
                {
                    terms.Add(word);
                }

                start = -1;
            }
        }

        return terms;
    }

    /// <summary>
    /// Scores a passage against a question's terms: how many different terms it contains matters most,
    /// how often they occur breaks ties. Zero means no term appears at all.
    /// </summary>
    public static double Score(string passage, IReadOnlyList<string> terms)
    {
        var distinct = 0;
        var occurrences = 0;
        foreach (var term in terms)
        {
            var count = CountOccurrences(passage, term);
            if (count > 0)
            {
                distinct++;
                occurrences += count;
            }
        }

        // Occurrences are capped so a passage repeating one word cannot outrank one holding two of them.
        return distinct == 0 ? 0 : distinct * 10 + Math.Min(occurrences, 9);
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (term.Length >= WholeWordBelowLength || IsWholeWord(text, index, term.Length))
            {
                count++;
            }

            index += term.Length;
        }

        return count;
    }

    private static bool IsWholeWord(string text, int index, int length) =>
        (index == 0 || !IsWordChar(text[index - 1]))
        && (index + length >= text.Length || !IsWordChar(text[index + length]));

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
