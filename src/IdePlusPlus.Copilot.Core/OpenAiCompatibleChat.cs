using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// The OpenAI-compatible chat-completions wire format: request message shaping and SSE stream
/// parsing. Shared by OpenRouterChatClient and LmStudioChatClient, which differ only in base URL
/// and whether an Authorization header is sent - the protocol itself is identical, not just
/// incidentally similar, so it lives here once rather than duplicated per client.
/// </summary>
public static class OpenAiCompatibleChat
{
    /// <summary>
    /// Most messages are just {role, content}. An assistant message that requested tool calls
    /// carries them as a tool_calls array instead (content may be empty); a tool-result message
    /// carries the result under the OpenAI "tool" role, linked back by tool_call_id.
    /// </summary>
    public static object ToRequestMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Assistant && message.ToolCalls is { Count: > 0 } toolCalls)
        {
            return new
            {
                role = "assistant",
                content = string.IsNullOrEmpty(message.Content) ? null : message.Content,
                tool_calls = toolCalls.Select(tc => new
                {
                    id = tc.Id,
                    type = "function",
                    function = new { name = tc.FunctionName, arguments = tc.ArgumentsJson },
                }),
            };
        }

        if (message.Role == ChatRole.Tool)
        {
            return new { role = "tool", tool_call_id = message.ToolCallId, content = message.Content };
        }

        return new { role = RoleName(message.Role), content = message.Content };
    }

    /// <summary>
    /// Null (rather than an empty/default object) when effort is None, so the request body omits
    /// the field entirely instead of sending an opinion the user never expressed.
    /// </summary>
    public static object? ToReasoningParameter(ChatEffort effort) => effort switch
    {
        ChatEffort.Low => new { effort = "low" },
        ChatEffort.Medium => new { effort = "medium" },
        ChatEffort.High => new { effort = "high" },
        _ => null,
    };

    public static object ToToolSchema(ToolDefinition tool) => new
    {
        type = "function",
        function = new
        {
            name = tool.Name,
            description = tool.Description,
            parameters = tool.ParametersSchema,
        },
    };

    /// <summary>
    /// "auto" (explicitly, rather than relying on whatever a given provider/model defaults to when
    /// the field is absent) when tools were offered at all, otherwise null - serialized as a literal
    /// JSON null when there are no tools (JsonContent.Create doesn't drop null properties), which an
    /// OpenAI-compatible API treats the same as the field being absent.
    /// </summary>
    public static string? ToToolChoice(IReadOnlyList<ToolDefinition>? tools) =>
        tools is { Count: > 0 } ? "auto" : null;

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    /// <summary>How long the stream may go without a single line (keepalive comments count) before the
    /// reply is given up on. Generous on purpose: a local model can think for a long time between
    /// tokens - this is only here so a connection that has silently died ends the turn with a message
    /// instead of leaving it on "Thinking..." until the user notices and presses Stop.</summary>
    public static readonly TimeSpan DefaultStreamIdleTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc cref="ReadSseStreamAsync(Stream, TimeSpan, CancellationToken)"/>
    public static IAsyncEnumerable<ChatStreamEvent> ReadSseStreamAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadSseStreamAsync(stream, DefaultStreamIdleTimeout, cancellationToken);

    /// <summary>
    /// Reads an OpenAI-compatible SSE response body, yielding parsed stream events. Validated
    /// against a real OpenRouter/Claude streaming response during M4/M5 - see those milestones'
    /// notes: tool-call arguments arrive as true incremental fragments (must be concatenated), and
    /// id/name are only present on a tool call's first delta for a given index.
    ///
    /// Every way the stream can end badly yields a <see cref="StreamError"/> rather than just stopping:
    /// a provider's in-band error, a connection that closes before the model said it was finished, and
    /// a stream that goes quiet. Ending silently is indistinguishable from a reply that completed, and
    /// the agent loop treats a finished reply as the end of the turn - so each of those used to look
    /// like the assistant giving up for no reason.
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamEvent> ReadSseStreamAsync(
        Stream stream, TimeSpan idleTimeout, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Set once the model has said it is done (finish_reason) or the provider has reported why it
        // cannot be - after either, the connection simply closing is expected, not a dropped reply.
        var concluded = false;

        while (true)
        {
            string? line = null;
            string? readError = null;
            idle.CancelAfter(idleTimeout);
            try
            {
                line = await reader.ReadLineAsync(idle.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                readError = $"No data was received for {idleTimeout.TotalMinutes:0.#} minute(s), so the reply was abandoned. "
                            + "The model server may have stalled or crashed.";
            }
            catch (IOException ex)
            {
                readError = $"Stream error: {ex.Message}";
            }

            if (readError is not null)
            {
                yield return new StreamError(readError);
                yield break;
            }

            if (line is null)
            {
                if (!concluded)
                {
                    yield return new StreamError(
                        "The connection closed before the model finished its reply, so it may be incomplete. "
                        + "The model server may have crashed or unloaded the model.");
                }

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

            foreach (var chunkEvent in ParseChunkEvents(data))
            {
                if (chunkEvent is FinishReasonReceived or StreamError)
                {
                    concluded = true;
                }

                yield return chunkEvent;
            }
        }
    }

    private static string DescribeProviderError(JsonNode error)
    {
        if (error is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return $"The provider reported an error: {text}";
        }

        var message = error["message"]?.ToString();
        var code = error["code"]?.ToString();
        return (message, code) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"The provider reported an error ({code}): {message}",
            ({ Length: > 0 }, _) => $"The provider reported an error: {message}",
            _ => $"The provider reported an error: {error.ToJsonString()}",
        };
    }

    private static IEnumerable<ChatStreamEvent> ParseChunkEvents(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            // Malformed or unexpected chunk shape - skip it rather than aborting the whole stream.
            yield break;
        }

        // A provider that fails after it has started streaming (OpenRouter does this when the upstream
        // model errors) cannot change the HTTP status any more, so it sends the error in-band as an
        // event with a top-level "error" and finish_reason "error". Skipped as an unrecognised chunk,
        // it left an empty reply that looked like the model choosing to say nothing.
        if (root?["error"] is { } error)
        {
            yield return new StreamError(DescribeProviderError(error));
        }

        // The usage-bearing chunk (when stream_options.include_usage is set) typically carries an
        // empty choices array, so this is checked independently of - not instead of - the delta
        // parsing below.
        if (root?["usage"] is { } usage)
        {
            yield return new UsageReceived(
                usage["prompt_tokens"]?.GetValue<int>() ?? 0,
                usage["completion_tokens"]?.GetValue<int>() ?? 0);
        }

        // The usage-bearing chunk (see above) carries an empty choices array - JsonArray's indexer
        // throws ArgumentOutOfRangeException rather than returning null for an out-of-bounds index,
        // so this must check Count before indexing rather than relying on ?[0] alone.
        var choices = root?["choices"]?.AsArray();
        var choice = choices is { Count: > 0 } ? choices[0] : null;

        // Read before the delta-null check below: the chunk that carries finish_reason often has an
        // empty (not null) delta, but some providers send it on its own trailing chunk with no delta
        // at all - either way this must not be skipped by the early-return that follows.
        if (choice?["finish_reason"]?.GetValue<string>() is { Length: > 0 } finishReason)
        {
            yield return new FinishReasonReceived(finishReason);

            // Said once: when an "error" object came with it, that already explained the failure.
            if (finishReason == "error" && root?["error"] is null)
            {
                yield return new StreamError("The provider reported an error while generating the reply.");
            }
        }

        var delta = choice?["delta"];
        if (delta is null)
        {
            yield break;
        }

        var content = delta["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content))
        {
            yield return new TextDelta(content);
        }

        // OpenRouter normalizes a reasoning-capable model's thinking trace into "reasoning";
        // DeepSeek's own (non-OpenRouter) API instead calls it "reasoning_content". Checking both
        // costs nothing for a provider that sends neither.
        var reasoning = delta["reasoning"]?.GetValue<string>() ?? delta["reasoning_content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(reasoning))
        {
            yield return new ReasoningDelta(reasoning);
        }

        var toolCalls = delta["tool_calls"]?.AsArray();
        if (toolCalls is null)
        {
            yield break;
        }

        foreach (var toolCall in toolCalls)
        {
            if (toolCall is null)
            {
                continue;
            }

            var index = toolCall["index"]?.GetValue<int>() ?? 0;
            var id = toolCall["id"]?.GetValue<string>();
            var functionName = toolCall["function"]?["name"]?.GetValue<string>();
            var argumentsFragment = toolCall["function"]?["arguments"]?.GetValue<string>();
            yield return new ToolCallDelta(index, id, functionName, argumentsFragment);
        }
    }
}
