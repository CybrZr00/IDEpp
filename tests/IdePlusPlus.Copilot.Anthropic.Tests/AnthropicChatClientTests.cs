using System.Net;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Anthropic.Tests;

public class AnthropicChatClientTests
{
    private static readonly ChatMessage[] OneUserMessage = [new(ChatRole.User, "Hi")];

    private const string ModelsJson = """
        {
          "data": [
            {
              "id": "claude-sonnet-5-5", "display_name": "Claude Sonnet 5.5", "lifecycle": "active", "max_tokens": 128000,
              "capabilities": {
                "thinking": { "types": { "adaptive": { "supported": true }, "enabled": { "supported": false } } },
                "effort": { "supported": true, "low": { "supported": true }, "medium": { "supported": true }, "high": { "supported": true } }
              }
            },
            { "id": "claude-old-retired", "lifecycle": "retired" },
            {
              "id": "claude-haiku-4-5-20251001", "lifecycle": "active", "max_tokens": 64000,
              "capabilities": {
                "thinking": { "types": { "adaptive": { "supported": false }, "enabled": { "supported": true } } },
                "effort": { "supported": false }
              }
            }
          ],
          "has_more": false,
          "last_id": "claude-haiku-4-5-20251001"
        }
        """;

    private const string TextStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":10,"cache_read_input_tokens":90,"cache_creation_input_tokens":5,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Pondering"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-1"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":0}

        event: ping
        data: {"type":"ping"}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":" world"}}

        event: content_block_stop
        data: {"type":"content_block_stop","index":1}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":42}}

        event: message_stop
        data: {"type":"message_stop"}

        """;

    private const string ToolStream = """
        data: {"type":"message_start","message":{"usage":{"input_tokens":5,"output_tokens":1}}}

        data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

        data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Need a file"}}

        data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig-abc"}}

        data: {"type":"content_block_stop","index":0}

        data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

        data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Reading."}}

        data: {"type":"content_block_stop","index":1}

        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_A","name":"read_file","input":{}}}

        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

        data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"a.cs\"}"}}

        data: {"type":"content_block_stop","index":2}

        data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"toolu_B","name":"list_files","input":{}}}

        data: {"type":"content_block_stop","index":3}

        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":20}}

        data: {"type":"message_stop"}

        """;

    private static SecureSettingsStore NewSettingsStore(string? apiKey = "test-key")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ideplusplus-test-secrets-{Guid.NewGuid():N}.json");
        var store = new SecureSettingsStore(path);
        if (apiKey is not null)
        {
            store.SetSecret(AnthropicChatClient.ApiKeySettingName, apiKey);
        }

        return store;
    }

    private static (AnthropicChatClient Client, RoutingHandler Handler) NewClient(params string[] messageStreams)
    {
        var handler = new RoutingHandler(ModelsJson, messageStreams);
        return (new AnthropicChatClient(new HttpClient(handler), NewSettingsStore()), handler);
    }

    private static async Task<List<ChatStreamEvent>> CollectAsync(
        AnthropicChatClient client,
        IReadOnlyList<ChatMessage>? messages = null,
        string model = "claude-sonnet-5-5",
        IReadOnlyList<ToolDefinition>? tools = null,
        ChatEffort effort = ChatEffort.None)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in client.StreamChatAsync(messages ?? OneUserMessage, model, tools, effort))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task StreamChatAsync_NoApiKey_YieldsErrorWithoutCallingApi()
    {
        var client = new AnthropicChatClient(new HttpClient(FakeHttpMessageHandler.ThatShouldNeverBeCalled()), NewSettingsStore(apiKey: null));

        var events = await CollectAsync(client);

        events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>().Message.ShouldContain("No Anthropic API key");
    }

    [Fact]
    public async Task StreamChatAsync_ParsesTextThinkingUsageAndFinish()
    {
        var (client, handler) = NewClient(TextStream);

        var events = await CollectAsync(client);

        events.OfType<TextDelta>().Select(d => d.Text).ShouldBe(["Hello", " world"]);
        events.OfType<ReasoningDelta>().Select(d => d.Text).ShouldBe(["Pondering"]);
        events.OfType<FinishReasonReceived>().ShouldHaveSingleItem().Reason.ShouldBe("stop");
        var usage = events.OfType<UsageReceived>().ShouldHaveSingleItem();
        usage.PromptTokens.ShouldBe(105);
        usage.CompletionTokens.ShouldBe(42);
        usage.CachedPromptTokens.ShouldBe(90);
        events[^1].ShouldBeOfType<StreamCompleted>();
        events.ShouldNotContain(e => e is StreamError);

        var request = handler.MessageRequests.ShouldHaveSingleItem();
        handler.LastHeaders!.GetValues("x-api-key").ShouldBe(["test-key"]);
        handler.LastHeaders!.Contains("anthropic-version").ShouldBeTrue();
        request["model"]!.GetValue<string>().ShouldBe("claude-sonnet-5-5");
        request["stream"]!.GetValue<bool>().ShouldBeTrue();
        request["cache_control"]!["type"]!.GetValue<string>().ShouldBe("ephemeral");
        request["thinking"].ShouldBeNull();
        request["output_config"].ShouldBeNull();
    }

    [Fact]
    public async Task StreamChatAsync_ToolUse_RenumbersToolCallsAndGivesEmptyArgumentsAnObject()
    {
        var (client, _) = NewClient(ToolStream);

        var events = await CollectAsync(client);

        var deltas = events.OfType<ToolCallDelta>().ToList();
        deltas[0].ShouldBe(new ToolCallDelta(0, "toolu_A", "read_file", null));
        string.Concat(deltas.Where(d => d.Index == 0).Select(d => d.ArgumentsFragment)).ShouldBe("{\"path\":\"a.cs\"}");
        deltas.First(d => d.Index == 1).Id.ShouldBe("toolu_B");
        string.Concat(deltas.Where(d => d.Index == 1).Select(d => d.ArgumentsFragment)).ShouldBe("{}");
        events.OfType<FinishReasonReceived>().ShouldHaveSingleItem().Reason.ShouldBe("tool_calls");
    }

    [Fact]
    public async Task StreamChatAsync_NextRequestOfToolTurn_ReplaysThinkingBlocksVerbatim()
    {
        var (client, handler) = NewClient(ToolStream, TextStream);
        await CollectAsync(client, effort: ChatEffort.High);

        ChatMessage[] followUp =
        [
            new(ChatRole.System, "Be helpful."),
            new(ChatRole.User, "Hi"),
            new(ChatRole.Assistant, "Reading.",
                [new ChatToolCall("toolu_A", "read_file", "{\"path\":\"a.cs\"}"), new ChatToolCall("toolu_B", "list_files", "{}")]),
            new(ChatRole.Tool, "file contents", ToolCallId: "toolu_A"),
            new(ChatRole.Tool, "", ToolCallId: "toolu_B"),
        ];
        await CollectAsync(client, followUp, effort: ChatEffort.High);

        var request = handler.MessageRequests[1];
        request["system"]![0]!["text"]!.GetValue<string>().ShouldBe("Be helpful.");
        var messages = request["messages"]!.AsArray();
        messages.Count.ShouldBe(3);

        var assistant = messages[1]!["content"]!.AsArray();
        assistant.Select(b => b!["type"]!.GetValue<string>()).ShouldBe(["thinking", "text", "tool_use", "tool_use"]);
        assistant[0]!["signature"]!.GetValue<string>().ShouldBe("sig-abc");
        assistant[0]!["thinking"]!.GetValue<string>().ShouldBe("Need a file");
        assistant[2]!["input"]!["path"]!.GetValue<string>().ShouldBe("a.cs");

        var results = messages[2]!["content"]!.AsArray();
        results.Select(b => b!["tool_use_id"]!.GetValue<string>()).ShouldBe(["toolu_A", "toolu_B"]);
        results[1]!["content"]!.GetValue<string>().ShouldBe("(no output)");

        request["thinking"]!["type"]!.GetValue<string>().ShouldBe("adaptive");
        request["output_config"]!["effort"]!.GetValue<string>().ShouldBe("high");
    }

    [Fact]
    public async Task StreamChatAsync_ManualThinkingModel_UsesBudgetAndNoEffort()
    {
        var (client, handler) = NewClient(TextStream);

        await CollectAsync(client, model: "claude-haiku-4-5-20251001", effort: ChatEffort.Medium);

        var request = handler.MessageRequests.ShouldHaveSingleItem();
        request["thinking"]!["type"]!.GetValue<string>().ShouldBe("enabled");
        request["thinking"]!["budget_tokens"]!.GetValue<int>().ShouldBe(8192);
        request["max_tokens"]!.GetValue<int>().ShouldBeGreaterThan(8192);
        request["output_config"].ShouldBeNull();
    }

    [Fact]
    public async Task StreamChatAsync_ManualThinkingWithoutReplayableTurn_LeavesThinkingOff()
    {
        var (client, handler) = NewClient(TextStream);
        ChatMessage[] midTurn =
        [
            new(ChatRole.User, "Hi"),
            new(ChatRole.Assistant, "", [new ChatToolCall("call:1", "read_file", "{}")]),
            new(ChatRole.Tool, "x", ToolCallId: "call:1"),
        ];

        await CollectAsync(client, midTurn, model: "claude-haiku-4-5-20251001", effort: ChatEffort.High);

        var request = handler.MessageRequests.ShouldHaveSingleItem();
        request["thinking"].ShouldBeNull();
        request["messages"]![1]!["content"]![0]!["id"]!.GetValue<string>().ShouldBe("call_1");
        request["messages"]![2]!["content"]![0]!["tool_use_id"]!.GetValue<string>().ShouldBe("call_1");
    }

    [Fact]
    public async Task StreamChatAsync_SendsToolsWithInputSchema()
    {
        var (client, handler) = NewClient(TextStream);
        var schema = JsonNode.Parse("""{"type":"object","properties":{"path":{"type":"string"}}}""")!;

        await CollectAsync(client, tools: [new ToolDefinition("read_file", "Reads a file", schema)]);

        var tool = handler.MessageRequests.ShouldHaveSingleItem()["tools"]![0]!;
        tool["name"]!.GetValue<string>().ShouldBe("read_file");
        tool["input_schema"]!["properties"]!["path"]!["type"]!.GetValue<string>().ShouldBe("string");
        handler.MessageRequests[0]["tool_choice"]!["type"]!.GetValue<string>().ShouldBe("auto");
    }

    [Fact]
    public async Task StreamChatAsync_HttpError_YieldsMessageFromErrorBody()
    {
        var handler = new RoutingHandler(ModelsJson, [])
        {
            MessagesStatus = HttpStatusCode.Unauthorized,
            MessagesErrorBody = """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""",
        };
        var client = new AnthropicChatClient(new HttpClient(handler), NewSettingsStore());

        var events = await CollectAsync(client);

        events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>().Message
            .ShouldBe("Anthropic returned 401: invalid x-api-key (authentication_error)");
    }

    [Fact]
    public async Task StreamChatAsync_InBandError_YieldsStreamError()
    {
        var (client, _) = NewClient("""
            data: {"type":"message_start","message":{"usage":{"input_tokens":1}}}

            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}

            """);

        var events = await CollectAsync(client);

        events[^1].ShouldBeOfType<StreamError>().Message.ShouldBe("Anthropic reported an error (overloaded_error): Overloaded");
    }

    [Fact]
    public async Task StreamChatAsync_ConnectionClosesEarly_YieldsStreamError()
    {
        var (client, _) = NewClient("""
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":"Hal"}}

            """);

        var events = await CollectAsync(client);

        events.OfType<TextDelta>().ShouldHaveSingleItem().Text.ShouldBe("Hal");
        events[^1].ShouldBeOfType<StreamError>();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_SkipsRetiredAndRecordsCapabilities()
    {
        var (client, handler) = NewClient();

        var models = await client.GetAvailableModelsAsync();

        models.ShouldBe(["claude-sonnet-5-5", "claude-haiku-4-5-20251001"]);
        handler.ModelRequestUris.ShouldHaveSingleItem().Query.ShouldContain("limit=1000");
        client.GetModelInfo("claude-haiku-4-5-20251001").SupportsAdaptiveThinking.ShouldBeFalse();
        client.GetModelInfo("claude-sonnet-5-5").MaxOutputTokens.ShouldBe(128000);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_NoApiKey_ReturnsEmpty()
    {
        var client = new AnthropicChatClient(new HttpClient(FakeHttpMessageHandler.ThatShouldNeverBeCalled()), NewSettingsStore(apiKey: null));

        (await client.GetAvailableModelsAsync()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("claude-opus-5-5", true, false, true)]
    [InlineData("claude-opus-4-6", true, false, true)]
    [InlineData("claude-sonnet-4-5-20250929", false, true, false)]
    [InlineData("claude-opus-4-5", false, true, true)]
    [InlineData("claude-sonnet-4-20250514", false, true, false)]
    [InlineData("claude-3-7-sonnet-20250219", false, true, false)]
    [InlineData("claude-3-haiku-20240307", false, false, false)]
    public void Guess_ClassifiesModelIds(string id, bool adaptive, bool manual, bool effort)
    {
        var info = AnthropicModelInfo.Guess(id);

        info.SupportsAdaptiveThinking.ShouldBe(adaptive);
        info.SupportsManualThinking.ShouldBe(manual);
        info.SupportsEffort.ShouldBe(effort);
    }

    [Fact]
    public async Task TestConnectionAsync_ReportsSuccessAndFailure()
    {
        var ok = new AnthropicChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, ModelsJson)), NewSettingsStore(null));
        (await ok.TestConnectionAsync("k")).ShouldBeNull();

        var bad = new AnthropicChatClient(
            new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""")),
            NewSettingsStore(null));
        (await bad.TestConnectionAsync("k"))!.ShouldContain("invalid x-api-key");
    }

    /// <summary>Answers /v1/models with a fixed list and each /v1/messages call with the next stream.</summary>
    private sealed class RoutingHandler(string modelsJson, string[] messageStreams) : HttpMessageHandler
    {
        private int _next;

        public List<JsonNode> MessageRequests { get; } = [];
        public List<Uri> ModelRequestUris { get; } = [];
        public System.Net.Http.Headers.HttpRequestHeaders? LastHeaders { get; private set; }
        public HttpStatusCode MessagesStatus { get; init; } = HttpStatusCode.OK;
        public string? MessagesErrorBody { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastHeaders = request.Headers;
            if (request.RequestUri!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                ModelRequestUris.Add(request.RequestUri);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(modelsJson) };
            }

            MessageRequests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            if (MessagesStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(MessagesStatus) { Content = new StringContent(MessagesErrorBody ?? "") };
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(messageStreams[_next++]) };
        }
    }
}
