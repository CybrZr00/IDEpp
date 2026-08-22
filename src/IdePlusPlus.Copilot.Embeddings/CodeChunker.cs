namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>Splits a file into overlapping line-range chunks small enough to embed usefully.</summary>
public static class CodeChunker
{
    private const int MaxLinesPerChunk = 120;
    private const int OverlapLines = 10;

    public static IEnumerable<CodeChunk> ChunkFile(string filePath, string content)
    {
        var lines = content.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || (lines.Length == 1 && lines[0].Length == 0))
        {
            yield break;
        }

        if (lines.Length <= MaxLinesPerChunk)
        {
            yield return new CodeChunk(filePath, 1, lines.Length, content);
            yield break;
        }

        var start = 0;
        while (start < lines.Length)
        {
            var end = Math.Min(start + MaxLinesPerChunk, lines.Length);
            var text = string.Join('\n', lines[start..end]);
            yield return new CodeChunk(filePath, start + 1, end, text);

            if (end >= lines.Length)
            {
                yield break;
            }

            start = end - OverlapLines;
        }
    }
}
