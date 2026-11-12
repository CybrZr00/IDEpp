using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;

namespace IdePlusPlus.Copilot.Anthropic;

/// <summary>
/// Claude through Anthropic's own Messages API (not the OpenAI-compatible shape the other providers
/// share). See <see cref="AnthropicRequestBuilder"/> and <see cref="AnthropicStreamReader"/> for the
/// wire format; this class adds the API key, model capabilities and the thinking-block replay cache.
/// </summary>
public sealed class AnthropicChatClient : IChatClient
{
    public const string ApiKeySettingName = "AnthropicApiKey";
    public const string DefaultModel = "claude-sonnet-5-5";
    public const string ApiVersion = "2023-06-01";

    private const string BaseUrl = "https://api.anthropic.com/v1/";
    private const string MessagesUrl = BaseUrl + "messages";
    private const string ModelsUrl = BaseUrl + "models";

    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Enough for a long agent session's worth of tool turns; the oldest go first, and a
    /// missing entry only costs the replay (see AnthropicRequestBuilder), never the request.</summary>
    private const int ReplayCacheCapacity = 4096;

    private readonly HttpClient _httpClient;
    private readonly SecureSettingsStore _settingsStore;
    private readonly ConcurrentDictionary<string, AnthropicModelInfo> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JsonArray> _replay = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _replayOrder = new();
    private int _modelFetchAttempted;

    public AnthropicChatClient(HttpClient httpClient, SecureSettingsStore settingsStore)
    {
        _httpClient = httpClient;
        _settingsStore = settingsStore;
    }

    public TimeSpan StreamIdleTimeout { get; init; } = AnthropicStreamReader.DefaultIdleTimeout;

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
            yield return new StreamError("No Anthropic API key set. Add one via Tools > Settings.");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(model))
        {
            yield return new StreamError("No Anthropic model selected.");
            yield break;
        }

        // Capabilities decide the request shape, so the list is fetched once even if the picker never
        // asked for it (a sub-agent or Ultra worker on Claude while the chat itself uses another provider).
        if (Interlocked.Exchange(ref _modelFetchAttempted, 1) == 0)
        {
            await FetchModelsAsync(apiKey, cancellationToken);
        }

        var body = AnthropicRequestBuilder.Build(messages, GetModelInfo(model.Trim()), tools, effort, LookupReplay);

        var (response, connectError) = await TryConnectAsync(body, apiKey, cancellationToken);
        if (connectError is not null)
        {
            yield return new StreamError(connectError);
            yield break;
        }

        using var httpResponse = response!;
        var stream = await httpResponse.Content.ReadAsStreamAsync(cancellationToken);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (var streamEvent in AnthropicStreamReader.ReadAsync(stream, StreamIdleTimeout, RememberToolTurn, cancellationToken))
            {
                yield return streamEvent;
            }
        }
    }

    /// <summary>
    /// Model ids from GET /v1/models, newest first, retired models left out. Needs the API key (the
    /// endpoint isn't public); returns an empty list without one or on any failure, like the other
    /// providers' model lists, so the picker keeps what it has and the id can still be typed.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _settingsStore.GetSecret(ApiKeySettingName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        Interlocked.Exchange(ref _modelFetchAttempted, 1);
        return (await FetchModelsAsync(apiKey, cancellationToken)).Select(m => m.Id).ToList();
    }

    /// <summary>Checks a key (typically one typed into Settings but not yet saved) with a minimal
    /// models request. Returns null on success, otherwise what went wrong.</summary>
    public async Task<string?> TestConnectionAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelListTimeout);
            using var request = NewRequest(HttpMethod.Get, ModelsUrl + "?limit=1", apiKey);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return $"Anthropic returned {(int)response.StatusCode}: {DescribeErrorBody(body)}";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The request to Anthropic timed out.";
        }
        catch (HttpRequestException ex)
        {
            return $"Request failed: {ex.Message}";
        }
    }

    /// <summary>What is known about a model: from the fetched list, else guessed from its id.</summary>
    public AnthropicModelInfo GetModelInfo(string model) =>
        _models.TryGetValue(model, out var info) ? info : AnthropicModelInfo.Guess(model);

    /// <summary>Split out from the HTTP call so the response-shape handling is unit-testable.</summary>
    public static (IReadOnlyList<AnthropicModelInfo> Models, bool HasMore, string? LastId) ParseModelsPage(string json)
    {
        var root = JsonNode.Parse(json);
        var models = (root?["data"]?.AsArray() ?? [])
            .Where(m => m?["lifecycle"]?.GetValue<string>() is not "retired")
            .Select(AnthropicModelInfo.Parse)
            .OfType<AnthropicModelInfo>()
            .ToList();

        var hasMore = root?["has_more"] is JsonValue more && more.TryGetValue<bool>(out var flag) && flag;
        var lastId = root?["last_id"] is JsonValue last && last.TryGetValue<string>(out var id) ? id : null;
        return (models, hasMore, lastId);
    }

    private async Task<IReadOnlyList<AnthropicModelInfo>> FetchModelsAsync(string apiKey, CancellationToken cancellationToken)
    {
        var all = new List<AnthropicModelInfo>();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelListTimeout);

            string? afterId = null;
            for (var page = 0; page < 10; page++)
            {
                var url = ModelsUrl + "?limit=1000" + (afterId is null ? "" : "&after_id=" + Uri.EscapeDataString(afterId));
                using var request = NewRequest(HttpMethod.Get, url, apiKey);
                using var response = await _httpClient.SendAsync(request, timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                var (models, hasMore, lastId) = ParseModelsPage(await response.Content.ReadAsStringAsync(timeout.Token));
                all.AddRange(models);
                if (!hasMore || lastId is null)
                {
                    break;
                }

                afterId = lastId;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException)
        {
            // Whatever arrived before the failure is still worth keeping.
        }

        foreach (var model in all)
        {
            _models[model.Id] = model;
        }

        return all;
    }

    private JsonArray? LookupReplay(string toolCallId) =>
        _replay.TryGetValue(toolCallId, out var content) ? content : null;

    private void RememberToolTurn(JsonArray content)
    {
        var firstToolId = content
            .Where(b => b?["type"]?.GetValue<string>() == "tool_use")
            .Select(b => b!["id"]?.GetValue<string>())
            .FirstOrDefault();
        if (string.IsNullOrEmpty(firstToolId))
        {
            return;
        }

        _replay[firstToolId] = content;
        _replayOrder.Enqueue(firstToolId);
        while (_replayOrder.Count > ReplayCacheCapacity && _replayOrder.TryDequeue(out var oldest))
        {
            _replay.TryRemove(oldest, out _);
        }
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private async Task<(HttpResponseMessage? Response, string? Error)> TryConnectAsync(
        JsonObject body, string apiKey, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, MessagesUrl, apiKey);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                response.Dispose();
                return (null, $"Anthropic returned {(int)response.StatusCode}: {DescribeErrorBody(text)}");
            }

            return (response, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The request to Anthropic timed out before it started answering.");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Request failed: {ex.Message}");
        }
    }

    /// <summary>Error bodies are <c>{"type":"error","error":{"type","message"}}</c>; the message is
    /// what's worth showing. Anything else is shown as it came.</summary>
    private static string DescribeErrorBody(string body)
    {
        try
        {
            if (JsonNode.Parse(body)?["error"] is JsonObject error && error["message"]?.GetValue<string>() is { Length: > 0 } message)
            {
                return error["type"]?.GetValue<string>() is { Length: > 0 } type ? $"{message} ({type})" : message;
            }
        }
        catch (JsonException)
        {
        }

        return body;
    }
}
