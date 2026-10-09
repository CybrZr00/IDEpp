using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Embeds text in-process with ONNX Runtime and bge-small-en-v1.5 (see
/// <see cref="LocalEmbeddingModelStore"/>), so codebase search needs no server running. The model
/// reads at most 512 tokens, hence <see cref="MaxChunkLines"/>; CLS-token pooling and an L2
/// normalisation are what the model was trained for.
/// </summary>
public sealed class OnnxEmbeddingClient : IEmbeddingClient, IDisposable
{
    private const int MaxTokens = 512;

    /// <summary>bge is trained to match a short question against a longer passage when the question
    /// carries this prefix; the passages (code) carry nothing.</summary>
    private const string QueryPrefix = "Represent this sentence for searching relevant passages: ";

    private readonly LocalEmbeddingModelStore _store;
    private readonly HttpClient _httpClient;
    private readonly object _loadLock = new();
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private string[] _inputNames = [];

    public OnnxEmbeddingClient(LocalEmbeddingModelStore store, HttpClient httpClient)
    {
        _store = store;
        _httpClient = httpClient;
    }

    public string ModelId => $"onnx:{LocalEmbeddingModelStore.ModelName}";

    // 512 tokens is roughly 40 lines of C#; any more and the model never sees the end of the chunk.
    public int MaxChunkLines => 40;

    public string UnavailableHint => "the built-in embedding model couldn't be loaded";

    public async Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken)
    {
        if (!_store.IsInstalled)
        {
            onProgress("Downloading the embedding model (about 34 MB, first time only)...");
            try
            {
                await _store.DownloadAsync(_httpClient, onProgress, cancellationToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                return $"couldn't download the embedding model ({ex.Message}). Check the connection and try again.";
            }
        }

        return await Task.Run(EnsureLoaded, cancellationToken) ? null : "the embedding model files are present but couldn't be loaded.";
    }

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false)
    {
        // No download here: this runs at the start of every chat turn, and a 34 MB fetch is not
        // something to spring on a user who only asked a question. Indexing does the download.
        if (!_store.IsInstalled)
        {
            return null;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));

        try
        {
            // Off the caller's thread: indexing is awaited from the UI thread, and inference is CPU work.
            return await Task.Run(() => EnsureLoaded() ? Embed(isQuery ? QueryPrefix + text : text) : null, timeoutSource.Token)
                .WaitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // Timed out, not stopped by the user.
        }
        catch (OnnxRuntimeException)
        {
            return null;
        }
    }

    private bool EnsureLoaded()
    {
        lock (_loadLock)
        {
            if (_session is not null)
            {
                return true;
            }

            if (!_store.IsInstalled)
            {
                return false;
            }

            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    // Half the cores: indexing runs while the user keeps working in the IDE.
                    IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2),
                };
                var session = new InferenceSession(_store.ModelPath, options);
                _tokenizer = BertTokenizer.Create(_store.VocabPath, new BertOptions { LowerCaseBeforeTokenization = true });
                _inputNames = session.InputMetadata.Keys.ToArray();
                _session = session;
                return true;
            }
            catch (Exception ex) when (ex is OnnxRuntimeException or IOException or ArgumentException or InvalidOperationException)
            {
                return false;
            }
        }
    }

    private float[] Embed(string text)
    {
        var ids = _tokenizer!.EncodeToIds(text, MaxTokens, out _, out _);
        var length = ids.Count;

        var inputIds = new DenseTensor<long>([1, length]);
        var attentionMask = new DenseTensor<long>([1, length]);
        var tokenTypeIds = new DenseTensor<long>([1, length]);
        for (var i = 0; i < length; i++)
        {
            inputIds[0, i] = ids[i];
            attentionMask[0, i] = 1;
        }

        var inputs = new List<NamedOnnxValue>(3);
        foreach (var name in _inputNames)
        {
            inputs.Add(name switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(name, inputIds),
                "attention_mask" => NamedOnnxValue.CreateFromTensor(name, attentionMask),
                "token_type_ids" => NamedOnnxValue.CreateFromTensor(name, tokenTypeIds),
                _ => throw new InvalidOperationException($"Unexpected model input '{name}'."),
            });
        }

        using var results = _session!.Run(inputs);
        var hidden = results.First().AsTensor<float>();

        // [1, tokens, dimensions] -> the first token's vector, scaled to unit length.
        var dimensions = hidden.Dimensions[2];
        var vector = new float[dimensions];
        var sumOfSquares = 0f;
        for (var d = 0; d < dimensions; d++)
        {
            vector[d] = hidden[0, 0, d];
            sumOfSquares += vector[d] * vector[d];
        }

        var norm = MathF.Sqrt(sumOfSquares);
        if (norm > 0)
        {
            for (var d = 0; d < dimensions; d++)
            {
                vector[d] /= norm;
            }
        }

        return vector;
    }

    public void Dispose() => _session?.Dispose();
}
