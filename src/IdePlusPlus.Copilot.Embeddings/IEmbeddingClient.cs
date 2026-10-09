namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Turns text into a vector for codebase search. Two implementations: the built-in ONNX model
/// (<see cref="OnnxEmbeddingClient"/>, the default) and an LM Studio server
/// (<see cref="LmStudioEmbeddingClient"/>). Every failure is a null result, never an exception - an
/// embedding only improves which code the model is shown, so it must never cost a chat turn.
/// </summary>
public interface IEmbeddingClient
{
    /// <summary>Names the vector space this client produces. Saved with an index so that one built
    /// by a different model (different dimensions, different geometry) is rebuilt, not searched.</summary>
    string ModelId { get; }

    /// <summary>Longest chunk, in lines, this model can embed without cutting the end off.</summary>
    int MaxChunkLines { get; }

    /// <summary>What to suggest checking when an embedding could not be had.</summary>
    string UnavailableHint { get; }

    /// <summary>Gets the client ready for a bulk indexing run - for the built-in model, downloads it
    /// the first time. Null when ready, otherwise why it is not.</summary>
    Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken);

    /// <summary>Null when there is no embedding to be had. <paramref name="isQuery"/> marks a search
    /// question rather than a piece of code, for models that embed the two differently.</summary>
    Task<float[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false);
}
