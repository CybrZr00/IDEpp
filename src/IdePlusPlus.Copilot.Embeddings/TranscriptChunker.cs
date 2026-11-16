using System.Text;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Splits a transcript entry's text into pieces small enough to embed. CodeChunker cuts by line count,
/// which suits code; conversation text is prose with the odd code block, so this packs whole
/// paragraphs up to a character budget instead and only cuts inside a paragraph when one alone is
/// over budget.
///
/// The budget is characters because no embedding client reports a token limit. 1200 characters is
/// well inside the built-in model's 512 tokens even for code-heavy text.
/// </summary>
public static class TranscriptChunker
{
    public const int DefaultMaxChars = 1200;

    public static IReadOnlyList<string> Chunk(string text, int maxChars = DefaultMaxChars)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        if (normalized.Length <= maxChars)
        {
            return [normalized];
        }

        var chunks = new List<string>();
        var current = new StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                chunks.Add(current.ToString());
                current.Clear();
            }
        }

        foreach (var rawParagraph in normalized.Split("\n\n"))
        {
            var paragraph = rawParagraph.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            if (paragraph.Length > maxChars)
            {
                Flush();
                chunks.AddRange(SplitLongParagraph(paragraph, maxChars));
                continue;
            }

            if (current.Length > 0 && current.Length + 2 + paragraph.Length > maxChars)
            {
                Flush();
            }

            if (current.Length > 0)
            {
                current.Append("\n\n");
            }

            current.Append(paragraph);
        }

        Flush();
        return chunks;
    }

    /// <summary>Cuts at a line break if there is one in the back half of the window, else at a space,
    /// else wherever the budget runs out - never between the two halves of a surrogate pair.</summary>
    private static IEnumerable<string> SplitLongParagraph(string paragraph, int maxChars)
    {
        var start = 0;
        while (start < paragraph.Length)
        {
            var remaining = paragraph.Length - start;
            if (remaining <= maxChars)
            {
                var tail = paragraph[start..].Trim();
                if (tail.Length > 0)
                {
                    yield return tail;
                }

                yield break;
            }

            var window = paragraph.AsSpan(start, maxChars);
            var floor = maxChars / 2;
            var cut = window[floor..].LastIndexOf('\n');
            if (cut < 0)
            {
                cut = window[floor..].LastIndexOf(' ');
            }

            var length = cut < 0 ? maxChars : floor + cut;
            if (length < maxChars && length > 0 && char.IsHighSurrogate(paragraph[start + length - 1]))
            {
                length--;
            }
            else if (cut < 0 && char.IsHighSurrogate(paragraph[start + maxChars - 1]))
            {
                length = maxChars - 1;
            }

            var piece = paragraph.Substring(start, length).Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            start += length;
        }
    }
}
