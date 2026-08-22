using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using Serilog;

namespace IdePlusPlus.Copilot.DeepSeek;

/// <summary>
/// Calls DeepSeek's native fill-in-the-middle completions endpoint directly (not via OpenRouter,
/// which has no suffix/FIM parameter - see the M0 research notes). GetCompletionAsync returns null
/// rather than throwing on failure - a missing inline suggestion is a non-event for the user, not an
/// error to interrupt typing over - but every failure is still logged via Serilog so "ghost text
/// never appears" is diagnosable after the fact instead of silent.
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

        using var request = BuildRequest(apiKey, prefix, suffix);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                Log.Warning("DeepSeek FIM request failed: {StatusCode} {Body}", (int)response.StatusCode, body);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var node = JsonNode.Parse(json);
            var text = node?["choices"]?[0]?["text"]?.GetValue<string>();
            return string.IsNullOrEmpty(text) ? null : text;
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "DeepSeek FIM request could not reach the server");
            return null;
        }
        catch (System.Text.Json.JsonException ex)
        {
            Log.Warning(ex, "DeepSeek FIM returned an unparseable response");
            return null;
        }
    }

    /// <summary>Used by the Settings window's "Test Connection" button - a real request with a
    /// trivial prompt, since DeepSeek has no dedicated health-check endpoint.</summary>
    public async Task<(bool Success, string Message)> TestConnectionAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var request = BuildRequest(apiKey, "int x = ", ";", maxTokens: 4);

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, "Connected successfully.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return (false, $"{(int)response.StatusCode}: {body}");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Could not reach DeepSeek: {ex.Message}");
        }
    }

    private static HttpRequestMessage BuildRequest(string apiKey, string prefix, string suffix, int maxTokens = MaxTokens)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, FimCompletionsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model = Model,
            prompt = prefix,
            suffix,
            max_tokens = maxTokens,
            echo = false,
        });

        return request;
    }
}
