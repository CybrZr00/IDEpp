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

    private static string RoleName(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    /// <summary>
    /// Reads an OpenAI-compatible SSE response body, yielding parsed stream events. Validated
    /// against a real OpenRouter/Claude streaming response during M4/M5 - see those milestones'
    /// notes: tool-call arguments arrive as true incremental fragments (must be concatenated), and
    /// id/name are only present on a tool call's first delta for a given index.
    /// </summary>
    public static async IAsyncEnumerable<ChatStreamEvent> ReadSseStreamAsync(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
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

            foreach (var chunkEvent in ParseChunkEvents(data))
            {
                yield return chunkEvent;
            }
        }
    }

    private static IEnumerable<ChatStreamEvent> ParseChunkEvents(string json)
    {
        JsonNode? delta;
        try
        {
            delta = JsonNode.Parse(json)?["choices"]?[0]?["delta"];
        }
        catch (Exception)
        {
            // Malformed or unexpected chunk shape - skip it rather than aborting the whole stream.
            yield break;
        }

        if (delta is null)
        {
            yield break;
        }

        var content = delta["content"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(content))
        {
            yield return new TextDelta(content);
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
