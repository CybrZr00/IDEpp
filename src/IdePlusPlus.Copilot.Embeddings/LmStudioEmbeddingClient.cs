using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Calls a local LM Studio server's OpenAI-compatible /v1/embeddings endpoint. No API key -
/// LM Studio's local server doesn't require auth. Failures return null; a missing embedding
/// just means that chunk/query doesn't participate in search, not a crash.
/// </summary>
public sealed class LmStudioEmbeddingClient
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

    public async Task<float[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken)
    {
        var model = _settingsStore.Get(ModelSettingName);
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var baseUrl = (_settingsStore.Get(BaseUrlSettingName) ?? DefaultBaseUrl).TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/embeddings")
        {
            Content = JsonContent.Create(new { input = text, model }),
        };

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var array = JsonNode.Parse(json)?["data"]?[0]?["embedding"]?.AsArray();
            return array?.Select(n => (float)n!.GetValue<double>()).ToArray();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
