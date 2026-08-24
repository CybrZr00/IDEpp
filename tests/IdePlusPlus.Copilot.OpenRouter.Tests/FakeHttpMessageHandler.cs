using System.Net;

namespace IdePlusPlus.Copilot.OpenRouter.Tests;

internal class FakeHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    /// <summary>The request body of the most recent SendAsync call - lets a test assert on what was
    /// actually sent (e.g. that tool_choice is present) without a real HTTP call.</summary>
    public string? LastRequestBody { get; private set; }

    public static FakeHttpMessageHandler ThatShouldNeverBeCalled() => new ThrowingHandler();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
    }

    private sealed class ThrowingHandler() : FakeHttpMessageHandler(HttpStatusCode.OK, "")
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP call should have been made.");
    }
}
