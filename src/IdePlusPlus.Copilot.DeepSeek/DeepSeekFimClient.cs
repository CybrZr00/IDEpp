using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.DeepSeek;

/// <summary>
/// Calls DeepSeek's native fill-in-the-middle completions endpoint directly (not via OpenRouter,
/// which has no suffix/FIM parameter - see the M0 research notes). Failures return null rather
/// than throwing: a missing inline suggestion is a non-event for the user, not an error to surface.
/// </summary>
public sealed class DeepSeekFimClient : IInlineCompletionProvider
{
    public const string ApiKeySettingName = "DeepSeekApiKey";

    private const string FimCompletionsUrl = "https://api.deepseek.com/beta/completions";
    private const string Model = "deepseek-chat";
    private const int MaxTokens = 256;

    private readonly HttpClient _httpClient;
    private readonly SecureSettingsStore _settingsStore;

    public DeepSeekFimClient(HttpClient httpClient, SecureSettingsStore settingsStore)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
    }

    public async Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken cancellationToken)
    {
        var apiKey = _settingsStore.GetSecret(ApiKeySettingName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, FimCompletionsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            prompt = prefix,
            suffix,
            max_tokens = MaxTokens,
            echo = false,
        });

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(json);
            var text = node?["choices"]?[0]?["text"]?.GetValue<string>();
            return string.IsNullOrEmpty(text) ? null : text;
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
