using System.Text.Json;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// A flat, in-memory list of embedded chunks with brute-force cosine similarity search.
/// A real vector database would be overkill at the file-count a solo project reaches -
/// this is fast enough up to at least tens of thousands of chunks.
/// </summary>
public sealed class VectorIndex
{
    private readonly List<CodeChunk> _chunks = [];

    public int Count => _chunks.Count;

    public void Clear() => _chunks.Clear();

    public void Add(CodeChunk chunk) => _chunks.Add(chunk);

    public IReadOnlyList<CodeChunk> Search(float[] queryEmbedding, int topK) =>
        _chunks
            .Where(c => c.Embedding is not null)
            .Select(c => (Chunk: c, Score: CosineSimilarity(queryEmbedding, c.Embedding!)))
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Chunk)
            .ToList();

    public void SaveToDisk(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(_chunks));
    }

    /// <summary>Replaces the contents with what is on disk. Cleared first, so a missing or corrupt
    /// file leaves the index empty rather than still holding whatever was loaded before - which, on a
    /// solution switch, was the previous solution's code.</summary>
    public void LoadFromDisk(string path)
    {
        _chunks.Clear();
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<List<CodeChunk>>(File.ReadAllText(path));
            if (loaded is not null)
            {
                _chunks.AddRange(loaded);
            }
        }
        catch (JsonException)
        {
            // Corrupt or incompatible index from a previous version - leave empty, re-index will rebuild it.
        }
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        var length = Math.Min(a.Length, b.Length);
        for (var i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        return normA == 0 || normB == 0 ? 0 : dot / (MathF.Sqrt(normA) * MathF.Sqrt(normB));
    }
}
