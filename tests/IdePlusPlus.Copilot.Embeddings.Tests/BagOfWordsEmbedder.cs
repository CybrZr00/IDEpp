namespace IdePlusPlus.Copilot.Embeddings.Tests;

/// <summary>
/// An embedding client whose vectors mean something: each word adds to one of 64 slots, so two texts
/// sharing words are closer than two that share none. Can be made unavailable (every call returns
/// null, as the real clients do when there is no model) and counts the calls it gets, which is how
/// tests tell "embedded again" from "loaded from disk".
/// </summary>
internal sealed class BagOfWordsEmbedder : IEmbeddingClient
{
    private const int Dimensions = 64;

    private int _passageCalls;
    private int _queryCalls;

    public string ModelId { get; set; } = "fake:a";

    public int MaxChunkLines => 40;

    public string UnavailableHint => "fake";

    public bool Available { get; set; } = true;

    /// <summary>Calls made to embed a stored passage.</summary>
    public int PassageCalls => Volatile.Read(ref _passageCalls);

    /// <summary>Calls made to embed a search question.</summary>
    public int QueryCalls => Volatile.Read(ref _queryCalls);

    public Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public Task<float[]?> GetEmbeddingAsync(
        string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false)
    {
        if (isQuery)
        {
            Interlocked.Increment(ref _queryCalls);
        }
        else
        {
            Interlocked.Increment(ref _passageCalls);
        }

        return Task.FromResult(Available ? Embed(text) : null);
    }

    private static float[] Embed(string text)
    {
        var vector = new float[Dimensions];
        foreach (var word in text.ToLowerInvariant().Split(
                     [' ', '\n', '\t', '.', ',', ';', ':', '(', ')', '"', '\''], StringSplitOptions.RemoveEmptyEntries))
        {
            // FNV-1a: the same word lands in the same slot in every process, unlike string.GetHashCode.
            var hash = 2166136261u;
            foreach (var c in word)
            {
                hash = (hash ^ c) * 16777619u;
            }

            vector[hash % Dimensions] += 1f;
        }

        return vector;
    }
}
