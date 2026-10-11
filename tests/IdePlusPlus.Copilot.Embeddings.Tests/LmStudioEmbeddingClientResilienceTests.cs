using System.Net;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

/// <summary>
/// GetEmbeddingAsync runs at the start of every chat turn, so none of its failure modes may cost the
/// turn anything: before, a cleared base URL threw and a slow server held the turn for 100 seconds.
/// </summary>
public sealed class LmStudioEmbeddingClientResilienceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusEmbeddingResilience", Guid.NewGuid().ToString("N"));
    private readonly AppSettingsStore _settings;
    private readonly SecureSettingsStore _secrets;

    public LmStudioEmbeddingClientResilienceTests()
    {
        Directory.CreateDirectory(_root);
        _settings = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        _secrets = new SecureSettingsStore(Path.Combine(_root, "secrets.json"));
        _settings.Set(LmStudioEmbeddingClient.ModelSettingName, "embed-model");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LmStudioEmbeddingClient ClientFor(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, _settings, _secrets);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankBaseUrl_FallsBackToTheDefault_InsteadOfThrowingOnARelativeUri(string blank)
    {
        _settings.Set(LmStudioEmbeddingClient.BaseUrlSettingName, blank);
        var handler = new RecordingHandler();

        var embedding = await ClientFor(handler).GetEmbeddingAsync("q", CancellationToken.None);

        embedding.ShouldBe([0.5f, 0.25f]);
        handler.LastUri.ShouldBe(new Uri($"{LmStudioEmbeddingClient.DefaultBaseUrl}/embeddings"));
    }

    [Fact]
    public async Task WithAnApiKey_SendsItAsABearerToken()
    {
        _secrets.SetSecret(LmStudioEmbeddingClient.ApiKeySettingName, " sk-test ");
        var handler = new RecordingHandler();

        await ClientFor(handler).GetEmbeddingAsync("q", CancellationToken.None);

        handler.LastAuthorization.ShouldNotBeNull();
        handler.LastAuthorization.Scheme.ShouldBe("Bearer");
        handler.LastAuthorization.Parameter.ShouldBe("sk-test");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutAnApiKey_SendsNoAuthorizationHeader(string? key)
    {
        if (key is not null)
        {
            _secrets.SetSecret(LmStudioEmbeddingClient.ApiKeySettingName, key);
        }

        var handler = new RecordingHandler();

        await ClientFor(handler).GetEmbeddingAsync("q", CancellationToken.None);

        handler.LastAuthorization.ShouldBeNull();
    }

    [Fact]
    public async Task ABaseUrlThatIsNotAnAddress_ReturnsNull_InsteadOfThrowing()
    {
        _settings.Set(LmStudioEmbeddingClient.BaseUrlSettingName, "localhost:1234/v1");

        var embedding = await ClientFor(new RecordingHandler()).GetEmbeddingAsync("q", CancellationToken.None);

        embedding.ShouldBeNull();
    }

    [Fact]
    public async Task ASlowServer_ReturnsNullOnceTheTimeoutPasses_InsteadOfHoldingTheCaller()
    {
        var started = DateTime.UtcNow;

        var embedding = await ClientFor(new HangingHandler())
            .GetEmbeddingAsync("q", CancellationToken.None, TimeSpan.FromMilliseconds(150));

        embedding.ShouldBeNull();
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task TheCallersOwnCancellation_StillPropagates()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ClientFor(new HangingHandler()).GetEmbeddingAsync("q", cts.Token, TimeSpan.FromSeconds(30)));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        public System.Net.Http.Headers.AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"embedding":[0.5,0.25]}]}"""),
            });
        }
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
