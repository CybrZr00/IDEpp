using System.Net;
using System.Net.Http.Headers;
using System.Text;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.LmStudio.Tests;

/// <summary>
/// The chat client talks to LM Studio's keyless local server by default, but the same settings point it
/// at any OpenAI-compatible server - including one that needs a key. The key must reach every request
/// (chat and model list), and a keyless setup must not send an Authorization header at all.
/// </summary>
public sealed class LmStudioChatClientTests : IDisposable
{
    private const string DoneStream = "data: [DONE]\n\n";

    private static readonly ChatMessage[] OneUserMessage = [new(ChatRole.User, "Hi")];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusLmStudioChatTests", Guid.NewGuid().ToString("N"));
    private readonly AppSettingsStore _settings;
    private readonly SecureSettingsStore _secrets;

    public LmStudioChatClientTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        _secrets = new SecureSettingsStore(Path.Combine(_root, "secrets.json"));
        _settings.Set(LmStudioChatClient.BaseUrlSettingName, "http://192.168.50.3:8080/v1");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LmStudioChatClient ClientFor(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, _settings, _secrets);

    [Fact]
    public async Task Chat_WithAnApiKey_SendsItAsABearerTokenToTheConfiguredServer()
    {
        _secrets.SetSecret(LmStudioChatClient.ApiKeySettingName, "  sk-test  ");
        var handler = new RecordingHandler(HttpStatusCode.OK, DoneStream);

        var events = await CollectAsync(ClientFor(handler));

        events.ShouldNotContain(e => e is StreamError);
        handler.LastUri.ShouldBe(new Uri("http://192.168.50.3:8080/v1/chat/completions"));
        handler.LastAuthorization.ShouldNotBeNull();
        handler.LastAuthorization.Scheme.ShouldBe("Bearer");
        handler.LastAuthorization.Parameter.ShouldBe("sk-test");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Chat_WithoutAnApiKey_SendsNoAuthorizationHeader(string? key)
    {
        if (key is not null)
        {
            _secrets.SetSecret(LmStudioChatClient.ApiKeySettingName, key);
        }

        var handler = new RecordingHandler(HttpStatusCode.OK, DoneStream);

        await CollectAsync(ClientFor(handler));

        handler.LastAuthorization.ShouldBeNull();
    }

    [Fact]
    public async Task ModelList_WithAnApiKey_SendsItToo_AndParsesTheIds()
    {
        _secrets.SetSecret(LmStudioChatClient.ApiKeySettingName, "sk-test");
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"data":[{"id":"qwen-a"},{"id":"qwen-b"}]}""");

        var models = await ClientFor(handler).GetAvailableModelsAsync();

        models.ShouldBe(["qwen-a", "qwen-b"]);
        handler.LastUri.ShouldBe(new Uri("http://192.168.50.3:8080/v1/models"));
        handler.LastAuthorization?.Parameter.ShouldBe("sk-test");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Chat_RejectedForAuth_TellsTheUserWhereToFixTheKey(HttpStatusCode status)
    {
        var handler = new RecordingHandler(status, "invalid api key");

        var events = await CollectAsync(ClientFor(handler));

        var error = events.OfType<StreamError>().ShouldHaveSingleItem();
        error.Message.ShouldContain($"{(int)status}");
        error.Message.ShouldContain("API key");
    }

    [Fact]
    public async Task Chat_RejectedForAnotherReason_DoesNotBlameTheKey()
    {
        var handler = new RecordingHandler(HttpStatusCode.InternalServerError, "boom");

        var events = await CollectAsync(ClientFor(handler));

        events.OfType<StreamError>().ShouldHaveSingleItem().Message.ShouldNotContain("API key");
    }

    private static async Task<List<ChatStreamEvent>> CollectAsync(LmStudioChatClient client)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var streamEvent in client.StreamChatAsync(OneUserMessage, "qwen"))
        {
            events.Add(streamEvent);
        }

        return events;
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8) });
        }
    }
}
