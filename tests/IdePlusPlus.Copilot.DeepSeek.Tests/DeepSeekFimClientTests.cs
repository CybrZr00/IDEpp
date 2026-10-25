using System.Net;
using System.Text.Json;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.DeepSeek.Tests;

public sealed class DeepSeekFimClientTests : IDisposable
{
    private readonly string _secretsPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-deepseek-fim-{Guid.NewGuid():N}.json");
    private readonly RecordingHandler _http = new();

    public void Dispose() => File.Delete(_secretsPath);

    private DeepSeekFimClient CreateClient(string? apiKey = "test-key")
    {
        var secrets = new SecureSettingsStore(_secretsPath);
        if (apiKey is not null)
        {
            secrets.SetSecret(DeepSeekFimClient.ApiKeySettingName, apiKey);
        }

        return new DeepSeekFimClient(new HttpClient(_http), secrets);
    }

    /// <summary>deepseek-chat was retired on 2026-07-24; FIM accepts only deepseek-flash or
    /// deepseek-v4-pro, so a retired name would leave inline suggestions silently empty.</summary>
    [Fact]
    public async Task GetCompletionAsync_AsksACurrentFimModel_ForTheMiddle()
    {
        _http.Body = """{ "choices": [ { "text": "42", "index": 0, "finish_reason": "stop" } ] }""";

        var completion = await CreateClient().GetCompletionAsync("int x = ", ";", CancellationToken.None);

        completion.ShouldBe("42");
        var (uri, authorization, body) = _http.Requests.ShouldHaveSingleItem();
        uri.ShouldBe(new Uri("https://api.deepseek.com/beta/completions"));
        authorization.ShouldBe("Bearer test-key");
        body.GetProperty("model").GetString().ShouldBe("deepseek-flash");
        body.GetProperty("prompt").GetString().ShouldBe("int x = ");
        body.GetProperty("suffix").GetString().ShouldBe(";");
    }

    [Fact]
    public async Task TestConnectionAsync_UsesTheSameModel()
    {
        var (success, _) = await CreateClient().TestConnectionAsync("typed-key", CancellationToken.None);

        success.ShouldBeTrue();
        var (_, authorization, body) = _http.Requests.ShouldHaveSingleItem();
        authorization.ShouldBe("Bearer typed-key");
        body.GetProperty("model").GetString().ShouldBe("deepseek-flash");
    }

    [Fact]
    public async Task GetCompletionAsync_WithNoKey_SendsNothing()
    {
        (await CreateClient(apiKey: null).GetCompletionAsync("a", "b", CancellationToken.None)).ShouldBeNull();
        _http.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetCompletionAsync_OnAnHttpError_IsNullRatherThanThrowing()
    {
        _http.Status = HttpStatusCode.BadRequest;
        _http.Body = """{"error":{"message":"Model Not Exist"}}""";

        (await CreateClient().GetCompletionAsync("a", "b", CancellationToken.None)).ShouldBeNull();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        public string Body { get; set; } = """{ "choices": [ { "text": "" } ] }""";

        public List<(Uri Uri, string? Authorization, JsonElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!, request.Headers.Authorization?.ToString(), JsonDocument.Parse(body).RootElement.Clone()));
            return new HttpResponseMessage(Status) { Content = new StringContent(Body) };
        }
    }
}
