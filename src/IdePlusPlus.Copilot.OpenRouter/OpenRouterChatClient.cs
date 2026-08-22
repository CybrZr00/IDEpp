using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.OpenRouter;

public sealed class OpenRouterChatClient : IChatClient
{
    public const string ApiKeySettingName = "OpenRouterApiKey";

    private const string ChatCompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";

    private readonly HttpClient _httpClient;
    private readonly SecureSettingsStore _settingsStore;

    public OpenRouterChatClient(HttpClient httpClient, SecureSettingsStore settingsStore)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _settingsStore.GetSecret(ApiKeySettingName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new StreamError("No OpenRouter API key set. Enter one above and click Save.");
            yield break;
        }

        var (response, connectError) = await TryConnectAsync(messages, model, apiKey, cancellationToken);
        if (connectError is not null)
        {
            yield return new StreamError(connectError);
            yield break;
        }

        using var httpResponse = response!;
        var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);

            while (true)
            {
                string? line = null;
                string? readError = null;
                try
                {
                    line = await reader.ReadLineAsync(cancellationToken);
                }
                catch (IOException ex)
                {
                    readError = ex.Message;
                }

                if (readError is not null)
                {
                    yield return new StreamError($"Stream error: {readError}");
                    yield break;
                }

                if (line is null)
                {
                    yield break;
                }

                // Blank lines separate SSE events; lines starting with ':' are comments/keepalive pings.
                if (line.Length == 0 || line[0] == ':' || !line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }

                var data = line["data:".Length..].Trim();
                if (data == "[DONE]")
                {
                    yield return new StreamCompleted();
                    yield break;
                }

                var delta = TryExtractDelta(data);
                if (!string.IsNullOrEmpty(delta))
                {
                    yield return new TextDelta(delta);
                }
            }
        }
    }

    private static string? TryExtractDelta(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["choices"]?[0]?["delta"]?["content"]?.GetValue<string>();
        }
        catch (Exception)
        {
            // Malformed or unexpected chunk shape - skip it rather than aborting the whole stream.
            return null;
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> TryConnectAsync(
        IReadOnlyList<ChatMessage> messages, string model, string apiKey, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            stream = true,
            messages = messages.Select(m => new { role = RoleName(m.Role), content = m.Content }),
        });

        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                return (null, $"OpenRouter returned {(int)response.StatusCode}: {body}");
            }

            return (response, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Request failed: {ex.Message}");
        }
    }

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        _ => "user",
    };
}
