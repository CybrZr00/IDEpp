using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// The embedding client the rest of the app uses: the built-in ONNX model unless Settings says to use
/// LM Studio. Re-reads the setting on every call, like the other clients, so a change in Settings
/// takes effect without a restart.
/// </summary>
public sealed class EmbeddingClientSelector : IEmbeddingClient
{
    public const string ProviderSettingName = "EmbeddingProvider";
    public const string LmStudioProvider = "LmStudio";

    private readonly AppSettingsStore _settingsStore;
    private readonly OnnxEmbeddingClient _onnx;
    private readonly LmStudioEmbeddingClient _lmStudio;

    public EmbeddingClientSelector(AppSettingsStore settingsStore, OnnxEmbeddingClient onnx, LmStudioEmbeddingClient lmStudio)
    {
        _settingsStore = settingsStore;
        _onnx = onnx;
        _lmStudio = lmStudio;
    }

    private IEmbeddingClient Current =>
        _settingsStore.Get(ProviderSettingName) == LmStudioProvider ? _lmStudio : _onnx;

    public string ModelId => Current.ModelId;

    public int MaxChunkLines => Current.MaxChunkLines;

    public string UnavailableHint => Current.UnavailableHint;

    public Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken) =>
        Current.PrepareAsync(onProgress, cancellationToken);

    public Task<float[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false) =>
        Current.GetEmbeddingAsync(text, cancellationToken, timeout, isQuery);
}
