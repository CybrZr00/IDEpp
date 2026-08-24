using System.Net;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.OpenRouter.Tests;

public class OpenRouterChatClientTests
{
    private static readonly ChatMessage[] OneUserMessage = [new(ChatRole.User, "Hi")];

    private static SecureSettingsStore NewSettingsStoreWithApiKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ideplusplus-test-secrets-{Guid.NewGuid():N}.json");
        var store = new SecureSettingsStore(path);
        store.SetSecret(OpenRouterChatClient.ApiKeySettingName, "test-key");
        return store;
    }

    [Fact]
    public async Task StreamChatAsync_ParsesDeltasAndSkipsPingsUntilDone()
    {
        const string sseBody = """
            data: {"choices":[{"delta":{"content":"Hello"}}]}

            : ping - keepalive

            data: {"choices":[{"delta":{"content":" world"}}]}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<TextDelta>().Select(d => d.Text).ShouldBe(["Hello", " world"]);
        events.ShouldContain(e => e is StreamCompleted);
        events.ShouldNotContain(e => e is StreamError);
    }

    [Fact]
    public async Task StreamChatAsync_ReasoningField_YieldsReasoningDeltaSeparateFromTextDelta()
    {
        const string sseBody = """
            data: {"choices":[{"delta":{"reasoning":"Let me "}}]}

            data: {"choices":[{"delta":{"reasoning":"think..."}}]}

            data: {"choices":[{"delta":{"content":"The answer is 4."}}]}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<ReasoningDelta>().Select(d => d.Text).ShouldBe(["Let me ", "think..."]);
        events.OfType<TextDelta>().Select(d => d.Text).ShouldBe(["The answer is 4."]);
    }

    [Fact]
    public async Task StreamChatAsync_ReasoningContentField_IsAlsoRecognized()
    {
        // DeepSeek's own (non-OpenRouter-normalized) API shape uses "reasoning_content" instead.
        const string sseBody = """
            data: {"choices":[{"delta":{"reasoning_content":"Thinking via the other field name"}}]}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<ReasoningDelta>().Select(d => d.Text).ShouldBe(["Thinking via the other field name"]);
    }

    [Fact]
    public async Task StreamChatAsync_UsageChunk_YieldsUsageReceivedAlongsideDeltas()
    {
        const string sseBody = """
            data: {"choices":[{"delta":{"content":"Hi"}}]}

            data: {"choices":[],"usage":{"prompt_tokens":42,"completion_tokens":7}}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<TextDelta>().Select(d => d.Text).ShouldBe(["Hi"]);
        var usage = events.OfType<UsageReceived>().ShouldHaveSingleItem();
        usage.PromptTokens.ShouldBe(42);
        usage.CompletionTokens.ShouldBe(7);
    }

    [Fact]
    public async Task StreamChatAsync_FinishReasonLength_YieldsFinishReasonReceived()
    {
        const string sseBody = """
            data: {"choices":[{"delta":{"content":"partial pl"}}]}

            data: {"choices":[{"delta":{},"finish_reason":"length"}]}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<FinishReasonReceived>().ShouldHaveSingleItem().Reason.ShouldBe("length");
    }

    [Fact]
    public async Task StreamChatAsync_FinishReasonStop_IsAlsoSurfaced()
    {
        const string sseBody = """
            data: {"choices":[{"delta":{"content":"done"}}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<FinishReasonReceived>().ShouldHaveSingleItem().Reason.ShouldBe("stop");
    }

    [Fact]
    public async Task StreamChatAsync_WithTools_SendsToolChoiceAuto()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "data: [DONE]\n\n");
        var client = new OpenRouterChatClient(new HttpClient(handler), NewSettingsStoreWithApiKey());
        var tools = new[] { new ToolDefinition("some_tool", "does something", System.Text.Json.Nodes.JsonNode.Parse("{}")!) };

        await foreach (var _ in client.StreamChatAsync(OneUserMessage, "test-model", tools))
        {
        }

        handler.LastRequestBody.ShouldNotBeNull().ShouldContain("\"tool_choice\":\"auto\"");
    }

    [Fact]
    public async Task StreamChatAsync_WithNoTools_SendsNullToolChoice()
    {
        var handler = new FakeHttpMessageHandler(HttpStatusCode.OK, "data: [DONE]\n\n");
        var client = new OpenRouterChatClient(new HttpClient(handler), NewSettingsStoreWithApiKey());

        await foreach (var _ in client.StreamChatAsync(OneUserMessage, "test-model"))
        {
        }

        handler.LastRequestBody.ShouldNotBeNull().ShouldContain("\"tool_choice\":null");
    }

    [Fact]
    public async Task StreamChatAsync_SkipsMalformedChunksWithoutAbortingTheStream()
    {
        const string sseBody = """
            data: {"choices":[{"delta":{"content":"A"}}]}

            data: this is not json

            data: {"choices":[{"delta":{"content":"B"}}]}

            data: [DONE]

            """;

        var client = new OpenRouterChatClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, sseBody)), NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        events.OfType<TextDelta>().Select(d => d.Text).ShouldBe(["A", "B"]);
    }

    [Fact]
    public async Task StreamChatAsync_WithNoApiKey_YieldsErrorWithoutCallingHttp()
    {
        var settingsStore = new SecureSettingsStore(Path.Combine(Path.GetTempPath(), $"ideplusplus-test-secrets-{Guid.NewGuid():N}.json"));
        var client = new OpenRouterChatClient(new HttpClient(FakeHttpMessageHandler.ThatShouldNeverBeCalled()), settingsStore);

        var events = await CollectAsync(client);

        var error = events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>();
        error.Message.ShouldContain("API key");
    }

    [Fact]
    public async Task StreamChatAsync_OnHttpErrorStatus_YieldsErrorWithStatusAndBody()
    {
        var client = new OpenRouterChatClient(
            new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.Unauthorized, "invalid api key")),
            NewSettingsStoreWithApiKey());

        var events = await CollectAsync(client);

        var error = events.ShouldHaveSingleItem().ShouldBeOfType<StreamError>();
        error.Message.ShouldContain("401");
        error.Message.ShouldContain("invalid api key");
    }

    // Mirrors the real GET /v1/models response shape: a "data" array whose entries carry "id" and a
    // "supported_parameters" list that advertises "tools" only for tool-capable models.
    private const string ModelsJson = """
        {
          "data": [
            { "id": "zzz/last-alphabetically", "supported_parameters": ["tools", "temperature"] },
            { "id": "anthropic/claude-sonnet-4.5", "supported_parameters": ["tools", "reasoning"] },
            { "id": "some/text-only-model", "supported_parameters": ["temperature"] },
            { "id": "some/no-parameters-listed" }
          ]
        }
        """;

    [Fact]
    public void ParseModelIds_ToolCapableOnly_ExcludesModelsWithoutToolSupport()
    {
        var ids = OpenRouterChatClient.ParseModelIds(ModelsJson, toolCapableOnly: true);

        ids.ShouldBe(["anthropic/claude-sonnet-4.5", "zzz/last-alphabetically"]);
    }

    [Fact]
    public void ParseModelIds_WithoutFiltering_IncludesEveryModel()
    {
        var ids = OpenRouterChatClient.ParseModelIds(ModelsJson, toolCapableOnly: false);

        ids.Count.ShouldBe(4);
        ids.ShouldContain("some/text-only-model");
        ids.ShouldContain("some/no-parameters-listed");
    }

    [Fact]
    public void ParseModelIds_SortsAlphabeticallySoTypeAheadIsPredictable()
    {
        var ids = OpenRouterChatClient.ParseModelIds(ModelsJson, toolCapableOnly: false);

        ids.ShouldBe(ids.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseModelIds_UnexpectedShape_ReturnsEmptyRatherThanThrowing()
    {
        OpenRouterChatClient.ParseModelIds("""{"error":"nope"}""", toolCapableOnly: true).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_OnHttpError_ReturnsEmptyRatherThanThrowing()
    {
        var client = new OpenRouterChatClient(
            new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom")),
            NewSettingsStoreWithApiKey());

        (await client.GetAvailableModelsAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAvailableModelsAsync_ParsesIdsFromASuccessfulResponse()
    {
        var client = new OpenRouterChatClient(
            new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, ModelsJson)),
            NewSettingsStoreWithApiKey());

        (await client.GetAvailableModelsAsync()).ShouldBe(["anthropic/claude-sonnet-4.5", "zzz/last-alphabetically"]);
    }

    private static async Task<List<ChatStreamEvent>> CollectAsync(OpenRouterChatClient client)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in client.StreamChatAsync(OneUserMessage, "test-model"))
        {
            events.Add(streamEvent);
        }

        return events;
    }
}
