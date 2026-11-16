using IdePlusPlus.Copilot.Embeddings;

namespace IdePlusPlus.Shell.Tests;

/// <summary>An embedding client with no model, as on a machine where the codebase was never indexed:
/// every embedding is null, which is how the real clients say "not available".</summary>
internal sealed class UnavailableEmbeddingClient : IEmbeddingClient
{
    public string ModelId => "none:test";

    public int MaxChunkLines => 40;

    public string UnavailableHint => "no model";

    public Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken) =>
        Task.FromResult<string?>("no model");

    public Task<float[]?> GetEmbeddingAsync(
        string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false) =>
        Task.FromResult<float[]?>(null);
}
