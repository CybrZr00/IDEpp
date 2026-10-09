using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.OpenRouter;

public sealed class OpenRouterChatClient : IChatClient
{
    public const string ApiKeySettingName = "OpenRouterApiKey";

    private const string ChatCompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";
    private const string ModelsUrl = "https://openrouter.ai/api/v1/models";

    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(15);

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

    /// <summary>
    /// Lists model ids OpenRouter currently offers (GET /v1/models), so the chat panel can populate
    /// its picker instead of relying on a hardcoded list that silently goes stale as models are
    /// added and retired. No API key needed - the endpoint is public, so the picker works before one
    /// is configured.
    ///
    /// <paramref name="toolCapableOnly"/> keeps only models whose supported_parameters advertise
    /// "tools". Every edit this IDE makes goes through a tool call, so a model without tool support
    /// cannot edit anything no matter how the request is phrased - offering those in the picker
    /// would just produce failures that look like IDE bugs rather than model limitations.
    ///
    /// Returns an empty list rather than throwing if the network call or response shape disappoints,
    /// matching LmStudioChatClient.GetAvailableModelsAsync - callers treat "no models" and
    /// "couldn't reach it" the same way (keep the current list, fall back to manual entry).
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(
        bool toolCapableOnly = true, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelListTimeout);
            using var response = await _httpClient.GetAsync(ModelsUrl, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            return ParseModelIds(json, toolCapableOnly);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Split out from the HTTP call so the response-shape handling is unit-testable.</summary>
    public static IReadOnlyList<string> ParseModelIds(string json, bool toolCapableOnly)
    {
        var data = JsonNode.Parse(json)?["data"]?.AsArray() ?? [];

        return data
            .Where(model => !toolCapableOnly || SupportsTools(model))
            .Select(model => model?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            // Alphabetical so the editable combo box's type-ahead lands somewhere predictable in a
            // list this long (OpenRouter currently offers a few hundred).
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool SupportsTools(JsonNode? model)
    {
        if (model?["supported_parameters"]?.AsArray() is not { } parameters)
        {
            return false;
        }

        return parameters.Any(p => p?.GetValue<string>() == "tools");
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The request to OpenRouter timed out before it started answering.");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Request failed: {ex.Message}");
        }
    }
}
