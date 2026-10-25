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

    /// <summary>OpenRouter filters the list itself when asked, which matters for more than size: the
    /// unfiltered response has been seen to stall part-way through its body (about 650 KB in, then
    /// nothing), so the picker timed out and came up empty, while this one returned whole in under a
    /// second.</summary>
    private const string ToolCapableModelsUrl = ModelsUrl + "?supported_parameters=tools";

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
    /// would just produce failures that look like IDE bugs rather than model limitations. It is
    /// asked of OpenRouter (see <see cref="ToolCapableModelsUrl"/>) and checked again here, so a
    /// server that ever ignores the parameter still cannot put a tool-less model in the picker.
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
            using var response = await _httpClient.GetAsync(toolCapableOnly ? ToolCapableModelsUrl : ModelsUrl, timeout.Token);
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

    /// <summary>
    /// OpenRouter's automatic prompt caching for Anthropic models: one top-level marker, which it
    /// places on the last cacheable block and moves forward as the conversation grows. An agent loop
    /// resends its whole history on every step, so every step after the first reads the earlier part
    /// back from the cache at a fraction of the price - and an Ultra team does that for every worker.
    /// Other providers cache matching prefixes on their own (OpenAI, DeepSeek) or ignore the field, so
    /// it is only set where it does something; elsewhere it goes as JSON null, which OpenRouter treats
    /// as absent - the same as <c>reasoning</c> at effort None.
    /// </summary>
    public static object? CacheControlFor(string model) =>
        model.TrimStart('~').StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase)
            ? new { type = "ephemeral" }
            : null;

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
            cache_control = CacheControlFor(model),
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
