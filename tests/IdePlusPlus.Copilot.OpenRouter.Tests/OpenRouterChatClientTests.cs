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
