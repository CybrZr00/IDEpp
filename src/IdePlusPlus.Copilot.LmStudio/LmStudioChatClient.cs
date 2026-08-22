using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.LmStudio;

/// <summary>
/// Chats against a local LM Studio server's OpenAI-compatible /v1/chat/completions endpoint - the
/// same wire format as OpenRouter (see OpenAiCompatibleChat), just no API key and a local base URL.
/// An alternative to OpenRouter for when the user doesn't have (or doesn't want to spend) an
/// OpenRouter key, at the cost of needing LM Studio running locally with a model loaded.
/// </summary>
public sealed class LmStudioChatClient : IChatClient
{
    public const string BaseUrlSettingName = "LmStudioBaseUrl";
    public const string DefaultBaseUrl = "http://localhost:1234/v1";

    private readonly HttpClient _httpClient;
    private readonly AppSettingsStore _settingsStore;

    public LmStudioChatClient(HttpClient httpClient, AppSettingsStore settingsStore)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseUrl = (_settingsStore.Get(BaseUrlSettingName) ?? DefaultBaseUrl).TrimEnd('/');

        var (response, connectError) = await TryConnectAsync(baseUrl, messages, model, tools, cancellationToken);
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

    /// <summary>
    /// Lists model ids the server currently knows about (GET /v1/models), so the chat panel can
    /// offer a picker instead of the user having to remember/retype an exact model id. Returns an
    /// empty list rather than throwing if the server is unreachable or the response is unexpected -
    /// callers treat "no models" and "server down" the same way (fall back to manual entry).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var baseUrl = (_settingsStore.Get(BaseUrlSettingName) ?? DefaultBaseUrl).TrimEnd('/');

        try
        {
            using var response = await _httpClient.GetAsync($"{baseUrl}/models", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var data = JsonNode.Parse(json)?["data"]?.AsArray() ?? [];
            return data
                .Select(m => m?["id"]?.GetValue<string>())
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => id!)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> TryConnectAsync(
        string baseUrl,
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        request.Content = JsonContent.Create(new
        {
            model,
            stream = true,
            messages = messages.Select(OpenAiCompatibleChat.ToRequestMessage),
            tools = tools is { Count: > 0 } ? tools.Select(OpenAiCompatibleChat.ToToolSchema) : null,
        });

        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                return (null, $"LM Studio returned {(int)response.StatusCode}: {body}");
            }

            return (response, null);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Could not reach LM Studio at {baseUrl} ({ex.Message}). Is it running with a model loaded?");
        }
    }
}
