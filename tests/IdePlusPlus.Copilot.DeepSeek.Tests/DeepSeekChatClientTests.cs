using System.Net;
using System.Text.Json;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.DeepSeek.Tests;

public sealed class DeepSeekChatClientTests : IDisposable
{
    private const string Reply = """
        data: {"choices":[{"delta":{"reasoning_content":"thinking it over"}}]}

        data: {"choices":[{"delta":{"content":"Hi there"}}]}

        data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

        data: [DONE]

        """;

    private readonly string _secretsPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-deepseek-{Guid.NewGuid():N}.json");
    private readonly ScriptedHandler _http = new();

    public void Dispose() => File.Delete(_secretsPath);

    private DeepSeekChatClient CreateClient(string? apiKey = "test-key")
    {
        var secrets = new SecureSettingsStore(_secretsPath);
        if (apiKey is not null)
        {
            secrets.SetSecret(DeepSeekFimClient.ApiKeySettingName, apiKey);
        }

        return new DeepSeekChatClient(new HttpClient(_http), secrets);
    }

    private static readonly ToolDefinition ReadFile =
        new("read_file", "Read a file", System.Text.Json.Nodes.JsonNode.Parse("""{ "type": "object" }""")!);

    private static async Task<List<ChatStreamEvent>> CollectAsync(
        DeepSeekChatClient client, IReadOnlyList<ChatMessage> messages, ChatEffort effort, IReadOnlyList<ToolDefinition>? tools = null)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in client.StreamChatAsync(messages, "deepseek-v4-flash", tools, effort))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    [Fact]
    public async Task WithNoApiKey_SaysSo_AndSendsNothing()
    {
        var events = await CollectAsync(CreateClient(apiKey: null), [new(ChatRole.User, "hi")], ChatEffort.None);

        events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>().Message.ShouldContain("DeepSeek API key");
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task EffortNone_TurnsThinkingOff_AndStreamsTheReply()
    {
        _http.Respond(HttpStatusCode.OK, Reply);

        var events = await CollectAsync(CreateClient(), [new(ChatRole.User, "hi")], ChatEffort.None);

        events.OfType<TextDelta>().Select(d => d.Text).ShouldBe(["Hi there"]);
        events.OfType<ReasoningDelta>().ShouldHaveSingleItem().Text.ShouldBe("thinking it over");
        var body = _http.Requests.ShouldHaveSingleItem().Body;
        body.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("disabled");
        body.GetProperty("reasoning_effort").ValueKind.ShouldBe(JsonValueKind.Null);
        _http.Requests[0].Uri.ShouldBe(new Uri("https://api.deepseek.com/chat/completions"));
        _http.Requests[0].Authorization.ShouldBe("Bearer test-key");
    }

    [Theory]
    [InlineData(ChatEffort.Low, "low")]
    [InlineData(ChatEffort.Medium, "high")]
    [InlineData(ChatEffort.High, "high")]
    [InlineData(ChatEffort.Ultra, "max")]
    public async Task AnyOtherEffort_TurnsThinkingOn_WithTheMappedEffort(ChatEffort effort, string expected)
    {
        _http.Respond(HttpStatusCode.OK, Reply);

        await CollectAsync(CreateClient(), [new(ChatRole.User, "hi")], effort);

        var body = _http.Requests.ShouldHaveSingleItem().Body;
        body.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("enabled");
        body.GetProperty("reasoning_effort").GetString().ShouldBe(expected);
    }

    /// <summary>DeepSeek answers 400 to a thinking-mode request with tools unless every earlier
    /// assistant message carries its reasoning_content.</summary>
    [Fact]
    public async Task WithThinkingOn_EveryAssistantMessageCarriesItsReasoningBack()
    {
        _http.Respond(HttpStatusCode.OK, Reply);
        ChatMessage[] history =
        [
            new(ChatRole.User, "look at Foo.cs"),
            new(ChatRole.Assistant, "", ToolCalls: [new ChatToolCall("call-1", "read_file", "{}")], ReasoningContent: "I should read it"),
            new(ChatRole.Tool, "class Foo {}", ToolCallId: "call-1"),
            new(ChatRole.Assistant, "It is empty."),
            new(ChatRole.User, "thanks"),
        ];

        await CollectAsync(CreateClient(), history, ChatEffort.High, [ReadFile]);

        var messages = _http.Requests.ShouldHaveSingleItem().Body.GetProperty("messages");
        messages[1].GetProperty("reasoning_content").GetString().ShouldBe("I should read it");
        messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString().ShouldBe("call-1");
        messages[3].GetProperty("reasoning_content").GetString().ShouldBe("");
        messages[0].TryGetProperty("reasoning_content", out _).ShouldBeFalse();
        messages[2].TryGetProperty("reasoning_content", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task WithThinkingOff_NoReasoningIsSentBack()
    {
        _http.Respond(HttpStatusCode.OK, Reply);

        await CollectAsync(
            CreateClient(), [new(ChatRole.User, "hi"), new(ChatRole.Assistant, "hello", ReasoningContent: "hmm")], ChatEffort.None);

        _http.Requests.ShouldHaveSingleItem().Body.GetProperty("messages")[1]
            .TryGetProperty("reasoning_content", out _).ShouldBeFalse();
    }

    /// <summary>A conversation that lost its reasoning - reloaded from disk, say - would otherwise
    /// fail every turn; the request is retried once with thinking off instead.</summary>
    [Fact]
    public async Task ARefusalNamingReasoningContent_IsRetriedOnceWithThinkingOff()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"error":{"message":"Missing reasoning_content in assistant message"}}""");
        _http.Respond(HttpStatusCode.OK, Reply);

        var events = await CollectAsync(CreateClient(), [new(ChatRole.User, "hi")], ChatEffort.High, [ReadFile]);

        events.OfType<TextDelta>().ShouldHaveSingleItem().Text.ShouldBe("Hi there");
        _http.Requests.Count.ShouldBe(2);
        _http.Requests[1].Body.GetProperty("thinking").GetProperty("type").GetString().ShouldBe("disabled");
    }

    [Fact]
    public async Task AnyOtherHttpError_EndsTheTurnWithTheStatusAndBody()
    {
        _http.Respond(HttpStatusCode.BadRequest, """{"error":{"message":"model not found"}}""");

        var events = await CollectAsync(CreateClient(), [new(ChatRole.User, "hi")], ChatEffort.High);

        var error = events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>();
        error.Message.ShouldContain("400");
        error.Message.ShouldContain("model not found");
        _http.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AnUnauthorizedKey_SaysWhereToFixIt()
    {
        _http.Respond(HttpStatusCode.Unauthorized, "bad key");

        var events = await CollectAsync(CreateClient(), [new(ChatRole.User, "hi")], ChatEffort.None);

        events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>().Message.ShouldContain("Tools > Settings");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_ListsTheKeysModels_Sorted()
    {
        _http.Respond(HttpStatusCode.OK, """
            { "object": "list", "data": [
              { "id": "deepseek-v4-pro", "object": "model", "owned_by": "deepseek" },
              { "id": "deepseek-v4-flash", "object": "model", "owned_by": "deepseek" }
            ] }
            """);

        var models = await CreateClient().GetAvailableModelsAsync();

        models.ShouldBe(["deepseek-v4-flash", "deepseek-v4-pro"]);
        _http.Requests.ShouldHaveSingleItem().Uri.ShouldBe(new Uri("https://api.deepseek.com/models"));
        _http.Requests[0].Authorization.ShouldBe("Bearer test-key");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_WithNoKeyOrAFailure_IsEmpty()
    {
        (await CreateClient(apiKey: null).GetAvailableModelsAsync()).ShouldBeEmpty();
        _http.Requests.ShouldBeEmpty();

        _http.Respond(HttpStatusCode.InternalServerError, "boom");
        (await CreateClient().GetAvailableModelsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public void ParseModelIds_UnexpectedShape_IsEmpty()
    {
        DeepSeekChatClient.ParseModelIds("""{"error":"nope"}""").ShouldBeEmpty();
        DeepSeekChatClient.ParseModelIds("""{"data":[{"id":5},{"name":"no id"}]}""").ShouldBeEmpty();
    }

    /// <summary>Answers each request with the next queued response, recording what was sent.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();

        public List<(Uri Uri, string? Authorization, JsonElement Body)> Requests { get; } = [];

        public void Respond(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), JsonDocument.Parse(body).RootElement.Clone()));

            var (status, responseBody) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, "unscripted");
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}
