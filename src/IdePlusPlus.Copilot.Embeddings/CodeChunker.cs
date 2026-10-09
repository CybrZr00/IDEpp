namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>Splits a file into overlapping line-range chunks small enough to embed usefully.</summary>
public static class CodeChunker
{
    public const int DefaultMaxLinesPerChunk = 120;
    public const int DefaultOverlapLines = 10;

    /// <summary>The overlap shrinks with the chunk: ten lines of a forty-line chunk is a quarter of it.</summary>
    public static IEnumerable<CodeChunk> ChunkFile(string filePath, string content, int maxLinesPerChunk = DefaultMaxLinesPerChunk)
    {
        var overlapLines = Math.Min(DefaultOverlapLines, maxLinesPerChunk / 8);

        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0))
        {
            yield break;
        }

        if (lines.Length <= maxLinesPerChunk)
        {
            yield return new CodeChunk(filePath, 1, lines.Length, content);
            yield break;
        }

        var start = 0;
        while (start < lines.Length)
        {
            var end = Math.Min(start + maxLinesPerChunk, lines.Length);
            var text = string.Join('\n', lines[start..end]);
            yield return new CodeChunk(filePath, start + 1, end, text);

            if (end >= lines.Length)
            {
                yield break;
            }

            start = end - overlapLines;
        }
    }
}
