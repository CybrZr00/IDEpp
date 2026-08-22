namespace IdePlusPlus.Copilot.Embeddings;

public sealed record CodeChunk(string FilePath, int StartLine, int EndLine, string Text)
{
    public float[]? Embedding { get; set; }
}
