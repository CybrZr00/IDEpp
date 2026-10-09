using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Calls a local LM Studio server's OpenAI-compatible /v1/embeddings endpoint. No API key -
/// LM Studio's local server doesn't require auth. Failures return null; a missing embedding
/// just means that chunk/query doesn't participate in search, not a crash.
/// </summary>
public sealed class LmStudioEmbeddingClient : IEmbeddingClient
{
    public const string BaseUrlSettingName = "LmStudioBaseUrl";
    public const string ModelSettingName = "LmStudioEmbeddingModel";
    public const string DefaultBaseUrl = "http://localhost:1234/v1";

    private readonly HttpClient _httpClient;
    private readonly AppSettingsStore _settingsStore;

    public LmStudioEmbeddingClient(HttpClient httpClient, AppSettingsStore settingsStore)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settingsStore.Get(ModelSettingName));

    public string ModelId => $"lmstudio:{_settingsStore.Get(ModelSettingName)?.Trim()}";

    // Embedding models served by LM Studio (nomic and similar) take thousands of tokens.
    public int MaxChunkLines => 120;

    public string UnavailableHint => "is LM Studio running with an embedding model loaded?";

    // Nothing to get ready: the server loads its model itself on the first request.
    public Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    /// <summary>For bulk indexing, where the very first request can include LM Studio loading the
    /// embedding model.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Null when there is no embedding to be had - not configured, unreachable, slow, or the server said
    /// no. Never throws for any of those: an embedding is an optional extra (it only improves which code
    /// the model is shown), and this runs at the start of every chat turn, so a failure here must cost
    /// the turn nothing. <paramref name="timeout"/> bounds the wait for the same reason - the caller's own
    /// token is the user pressing Stop, which is not a failure and still propagates.
    /// </summary>
    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false)
    {
        var model = _settingsStore.Get(ModelSettingName);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        // IsNullOrWhiteSpace, not "?? Default": a base URL cleared in Settings is stored as an empty
        // string, which "??" keeps - and an empty base URL makes the request URI relative, which
        // HttpClient rejects with an InvalidOperationException.
        var configuredUrl = _settingsStore.Get(BaseUrlSettingName);
        var baseUrl = (string.IsNullOrWhiteSpace(configuredUrl) ? DefaultBaseUrl : configuredUrl).Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings")
        {
            Content = JsonContent.Create(new { input = text, model }),
        };

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);

        try
        {
            using var response = await _httpClient.SendAsync(request, timeoutSource.Token);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(timeoutSource.Token);
            var array = JsonNode.Parse(json)?["data"]?[0]?["embedding"]?.AsArray();
            return array?.Select(n => (float)n!.GetValue<double>()).ToArray();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null; // Timed out, not stopped by the user.
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
