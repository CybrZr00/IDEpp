using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
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
        IReadOnlyList<ToolDefinition>? tools = null,
        ChatEffort effort = ChatEffort.None,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var apiKey = _settingsStore.GetSecret(ApiKeySettingName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new StreamError("No OpenRouter API key set. Add one via Tools > Settings.");
            yield break;
        }

        var (response, connectError) = await TryConnectAsync(messages, model, tools, effort, apiKey, cancellationToken);
        if (connectError is not null)
        {
            yield return new StreamError(connectError);
            yield break;
        }

        using var httpResponse = response!;
        var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (var streamEvent in OpenAiCompatibleChat.ReadSseStreamAsync(stream, cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> TryConnectAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools,
        ChatEffort effort,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = JsonContent.Create(new
        {
            model,
            stream = true,
            stream_options = new { include_usage = true },
            messages = messages.Select(OpenAiCompatibleChat.ToRequestMessage),
            tools = tools is { Count: > 0 } ? tools.Select(OpenAiCompatibleChat.ToToolSchema) : null,
            tool_choice = OpenAiCompatibleChat.ToToolChoice(tools),
            reasoning = OpenAiCompatibleChat.ToReasoningParameter(effort),
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
}
