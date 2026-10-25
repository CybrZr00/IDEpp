using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;
using Serilog;

namespace IdePlusPlus.Copilot.DeepSeek;

/// <summary>
/// Chats with DeepSeek's own API (api.deepseek.com) using the same key as inline completion. The
/// wire format is OpenAI-compatible (see OpenAiCompatibleChat), with two DeepSeek specifics:
///
/// - Thinking is a request field of its own, on by default server-side. Effort None turns it off
///   (thinking: disabled); any other level turns it on with reasoning_effort - Ultra as "max".
/// - In thinking mode, a request that offers tools must carry every earlier assistant message's
///   reasoning_content, or DeepSeek answers 400. ChatMessage.ReasoningContent carries it, and is
///   sent here and nowhere else. A conversation that lost some of it (one reloaded from disk keeps
///   no reasoning) would then fail every turn, so a 400 that names reasoning_content is retried once
///   with thinking off rather than ending the turn.
/// </summary>
public sealed class DeepSeekChatClient : IChatClient
{
    /// <summary>DeepSeek's current name for V4.1 Flash. deepseek-v4-flash, the name before it, is
    /// only routed here for compatibility, and deepseek-chat/-reasoner are retired.</summary>
    public const string DefaultModel = "deepseek-flash";

    private const string ChatCompletionsUrl = "https://api.deepseek.com/chat/completions";
    private const string ModelsUrl = "https://api.deepseek.com/models";

    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly SecureSettingsStore _settingsStore;

    public DeepSeekChatClient(HttpClient httpClient, SecureSettingsStore settingsStore)
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
        var apiKey = _settingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            yield return new StreamError("No DeepSeek API key set. Add one via Tools > Settings.");
            yield break;
        }

        var thinking = effort != ChatEffort.None;
        var (response, error, reasoningRejected) = await TryConnectAsync(messages, model, tools, effort, thinking, apiKey, cancellationToken);
        if (reasoningRejected)
        {
            Log.Warning("DeepSeek refused the conversation's reasoning history; retrying this request with thinking off");
            (response, error, _) = await TryConnectAsync(messages, model, tools, effort, thinking: false, apiKey, cancellationToken);
        }

        if (error is not null)
        {
            yield return new StreamError(error);
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
    /// The models the key can use (GET /models), for the chat pane's picker. Empty rather than an
    /// exception when there is no key, the request fails, or the shape is unexpected - the picker is
    /// editable, so a known model id still works.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
    {
        var apiKey = _settingsStore.GetSecret(DeepSeekFimClient.ApiKeySettingName);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return [];
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelListTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            return ParseModelIds(await response.Content.ReadAsStringAsync(timeout.Token));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<string> ParseModelIds(string json)
    {
        if (JsonNode.Parse(json)?["data"] is not JsonArray data)
        {
            return [];
        }

        return data
            .Select(model => model?["id"] is JsonValue id && id.TryGetValue<string>(out var text) ? text : null)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>OpenRouter-style effort names mapped onto DeepSeek's low/high/max - Medium has no
    /// DeepSeek equivalent of its own (the API maps "medium" to high anyway), and Ultra asks for
    /// the most thinking it offers.</summary>
    public static string ToReasoningEffort(ChatEffort effort) => effort switch
    {
        ChatEffort.Low => "low",
        ChatEffort.Ultra => "max",
        _ => "high",
    };

    /// <summary>OpenAiCompatibleChat's message shape, plus reasoning_content on assistant messages
    /// when thinking is on - the one field DeepSeek needs that no other provider here wants.</summary>
    public static object ToRequestMessage(ChatMessage message, bool thinking)
    {
        if (!thinking || message.Role != ChatRole.Assistant)
        {
            return OpenAiCompatibleChat.ToRequestMessage(message);
        }

        var node = System.Text.Json.JsonSerializer.SerializeToNode(OpenAiCompatibleChat.ToRequestMessage(message))!.AsObject();
        node["reasoning_content"] = message.ReasoningContent ?? "";
        return node;
    }

    private async Task<(HttpResponseMessage? Response, string? Error, bool ReasoningRejected)> TryConnectAsync(
        IReadOnlyList<ChatMessage> messages,
        string model,
        IReadOnlyList<ToolDefinition>? tools,
        ChatEffort effort,
        bool thinking,
        string apiKey,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Content = JsonContent.Create(new
        {
            model,
            stream = true,
            stream_options = new { include_usage = true },
            messages = messages.Select(m => ToRequestMessage(m, thinking)),
            tools = tools is { Count: > 0 } ? tools.Select(OpenAiCompatibleChat.ToToolSchema) : null,
            tool_choice = OpenAiCompatibleChat.ToToolChoice(tools),
            thinking = new { type = thinking ? "enabled" : "disabled" },
            reasoning_effort = thinking ? ToReasoningEffort(effort) : null,
        });

        try
        {
            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var status = response.StatusCode;
                response.Dispose();
                var reasoningRejected = thinking
                                        && status == HttpStatusCode.BadRequest
                                        && body.Contains("reasoning_content", StringComparison.OrdinalIgnoreCase);
                var hint = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? " Check the DeepSeek API key under Tools > Settings."
                    : status == HttpStatusCode.PaymentRequired
                        ? " The DeepSeek account has run out of balance."
                        : "";
                return (null, $"DeepSeek returned {(int)status}: {body}{hint}", reasoningRejected);
            }

            return (response, null, false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "The request to DeepSeek timed out before it started answering.", false);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Request failed: {ex.Message}", false);
        }
    }
}
