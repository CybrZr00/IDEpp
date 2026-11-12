using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Copilot.Anthropic;

/// <summary>
/// Reads a Messages API SSE stream into the provider-neutral events. Claude streams content blocks
/// (text, thinking, tool_use) each with its own index; tool calls are renumbered 0, 1, 2... because
/// the agent loops key tool calls on index and expect them dense, while Claude's block indices count
/// text and thinking blocks too.
///
/// Like OpenAiCompatibleChat.ReadSseStreamAsync, every bad ending yields a <see cref="StreamError"/>:
/// an in-band error event, a connection that closes before <c>message_stop</c>, and a stream that goes
/// quiet - a silent ending would look like a finished reply and end the agent's turn.
/// </summary>
public static class AnthropicStreamReader
{
    public static readonly TimeSpan DefaultIdleTimeout = OpenAiCompatibleChat.DefaultStreamIdleTimeout;

    /// <param name="onToolTurn">Called at the end of a response that made tool calls, with the
    /// complete content blocks as Claude sent them (thinking signatures included), for replay on the
    /// next request of the same turn.</param>
    public static async IAsyncEnumerable<ChatStreamEvent> ReadAsync(
        Stream stream,
        TimeSpan idleTimeout,
        Action<JsonArray>? onToolTurn,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var state = new StreamState();

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
                readError = $"No data was received from Anthropic for {idleTimeout.TotalMinutes:0.#} minute(s), so the reply was abandoned.";
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
                if (!state.Concluded)
                {
                    yield return new StreamError(
                        "The connection to Anthropic closed before the model finished its reply, so it may be incomplete.");
                }

                yield break;
            }

            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            JsonNode? data;
            try
            {
                data = JsonNode.Parse(line["data:".Length..].Trim());
            }
            catch (Exception)
            {
                continue;
            }

            if (data is null)
            {
                continue;
            }

            foreach (var streamEvent in state.Handle(data, onToolTurn))
            {
                yield return streamEvent;
            }

            if (state.Finished)
            {
                yield break;
            }
        }
    }

    /// <summary>Claude's stop_reason in the vocabulary the agent loops already understand from the
    /// OpenAI-compatible providers - "length" in particular is what tells them the reply was cut off
    /// rather than finished.</summary>
    public static string MapStopReason(string stopReason) => stopReason switch
    {
        "end_turn" or "stop_sequence" or "pause_turn" => "stop",
        "tool_use" => "tool_calls",
        "max_tokens" or "model_context_window_exceeded" => "length",
        "refusal" => "content_filter",
        _ => stopReason,
    };

    private sealed class StreamState
    {
        private readonly Dictionary<int, Block> _blocks = [];
        private int _nextToolIndex;
        private int _inputTokens;
        private int _cacheReadTokens;
        private int _cacheWriteTokens;
        private int _outputTokens;
        private bool _hasUsage;

        public bool Concluded { get; private set; }

        public bool Finished { get; private set; }

        public IEnumerable<ChatStreamEvent> Handle(JsonNode data, Action<JsonArray>? onToolTurn)
        {
            switch (Str(data["type"]))
            {
                case "message_start":
                    ReadUsage(data["message"]?["usage"]);
                    break;

                case "content_block_start":
                    foreach (var e in StartBlock(Int(data["index"]), data["content_block"]))
                    {
                        yield return e;
                    }

                    break;

                case "content_block_delta":
                    foreach (var e in Delta(Int(data["index"]), data["delta"]))
                    {
                        yield return e;
                    }

                    break;

                case "content_block_stop":
                    if (_blocks.TryGetValue(Int(data["index"]), out var stopped)
                        && stopped.Type == "tool_use" && stopped.Json.Length == 0)
                    {
                        // A call with no arguments streams no input_json_delta at all; the loops parse
                        // the arguments as JSON, so they get an empty object rather than nothing.
                        stopped.Json.Append("{}");
                        yield return new ToolCallDelta(stopped.ToolIndex, null, null, "{}");
                    }

                    break;

                case "message_delta":
                    ReadUsage(data["usage"]);
                    if (Str(data["delta"]?["stop_reason"]) is { Length: > 0 } stopReason)
                    {
                        Concluded = true;
                        yield return new FinishReasonReceived(MapStopReason(stopReason));
                    }

                    break;

                case "message_stop":
                    if (_hasUsage)
                    {
                        yield return new UsageReceived(
                            _inputTokens + _cacheReadTokens + _cacheWriteTokens, _outputTokens, _cacheReadTokens);
                    }

                    if (_nextToolIndex > 0)
                    {
                        onToolTurn?.Invoke(BuildContent());
                    }

                    Concluded = true;
                    Finished = true;
                    yield return new StreamCompleted();
                    break;

                case "error":
                    Concluded = true;
                    Finished = true;
                    yield return new StreamError(DescribeError(data["error"]));
                    break;
            }
        }

        private IEnumerable<ChatStreamEvent> StartBlock(int index, JsonNode? block)
        {
            var type = Str(block?["type"]) ?? "";
            var entry = new Block(type);
            _blocks[index] = entry;

            switch (type)
            {
                case "text":
                    if (Str(block?["text"]) is { Length: > 0 } text)
                    {
                        entry.Text.Append(text);
                        yield return new TextDelta(text);
                    }

                    break;

                case "thinking":
                    if (Str(block?["thinking"]) is { Length: > 0 } thinking)
                    {
                        entry.Text.Append(thinking);
                        yield return new ReasoningDelta(thinking);
                    }

                    entry.Signature = Str(block?["signature"]);
                    break;

                case "redacted_thinking":
                    entry.Data = Str(block?["data"]);
                    break;

                case "tool_use":
                    entry.ToolIndex = _nextToolIndex++;
                    entry.Id = Str(block?["id"]);
                    entry.Name = Str(block?["name"]);
                    yield return new ToolCallDelta(entry.ToolIndex, entry.Id, entry.Name, null);
                    break;
            }
        }

        private IEnumerable<ChatStreamEvent> Delta(int index, JsonNode? delta)
        {
            if (!_blocks.TryGetValue(index, out var entry))
            {
                yield break;
            }

            switch (Str(delta?["type"]))
            {
                case "text_delta" when Str(delta?["text"]) is { Length: > 0 } text:
                    entry.Text.Append(text);
                    yield return new TextDelta(text);
                    break;

                case "thinking_delta" when Str(delta?["thinking"]) is { Length: > 0 } thinking:
                    entry.Text.Append(thinking);
                    yield return new ReasoningDelta(thinking);
                    break;

                case "signature_delta":
                    entry.Signature = (entry.Signature ?? "") + Str(delta?["signature"]);
                    break;

                case "input_json_delta" when Str(delta?["partial_json"]) is { Length: > 0 } json:
                    entry.Json.Append(json);
                    yield return new ToolCallDelta(entry.ToolIndex, null, null, json);
                    break;
            }
        }

        private JsonArray BuildContent()
        {
            var content = new JsonArray();
            foreach (var (_, block) in _blocks.OrderBy(b => b.Key))
            {
                JsonObject? node = block.Type switch
                {
                    "thinking" => new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = block.Text.ToString(),
                        ["signature"] = block.Signature ?? "",
                    },
                    "redacted_thinking" => new JsonObject { ["type"] = "redacted_thinking", ["data"] = block.Data ?? "" },
                    "text" when block.Text.Length > 0 => new JsonObject { ["type"] = "text", ["text"] = block.Text.ToString() },
                    "tool_use" => new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = block.Id,
                        ["name"] = block.Name,
                        ["input"] = ParseInput(block.Json.ToString()),
                    },
                    _ => null,
                };

                if (node is not null)
                {
                    content.Add(node);
                }
            }

            return content;
        }

        private void ReadUsage(JsonNode? usage)
        {
            if (usage is null)
            {
                return;
            }

            _hasUsage = true;
            _inputTokens = Math.Max(_inputTokens, Int(usage["input_tokens"]));
            _cacheReadTokens = Math.Max(_cacheReadTokens, Int(usage["cache_read_input_tokens"]));
            _cacheWriteTokens = Math.Max(_cacheWriteTokens, Int(usage["cache_creation_input_tokens"]));
            _outputTokens = Math.Max(_outputTokens, Int(usage["output_tokens"]));
        }

        private static JsonNode ParseInput(string json)
        {
            try
            {
                return JsonNode.Parse(json) as JsonObject ?? new JsonObject();
            }
            catch (Exception)
            {
                return new JsonObject();
            }
        }
    }

    public static string DescribeError(JsonNode? error)
    {
        var type = Str(error?["type"]);
        var message = Str(error?["message"]);
        return (type, message) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"Anthropic reported an error ({type}): {message}",
            (_, { Length: > 0 }) => $"Anthropic reported an error: {message}",
            _ => $"Anthropic reported an error: {error?.ToJsonString() ?? "(no details)"}",
        };
    }

    private static string? Str(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Int(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : 0;

    private sealed class Block(string type)
    {
        public string Type { get; } = type;
        public StringBuilder Text { get; } = new();
        public StringBuilder Json { get; } = new();
        public string? Signature { get; set; }
        public string? Data { get; set; }
        public string? Id { get; set; }
        public string? Name { get; set; }
        public int ToolIndex { get; set; }
    }
}
