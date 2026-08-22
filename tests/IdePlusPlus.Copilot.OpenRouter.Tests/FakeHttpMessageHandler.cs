using System.Net;

namespace IdePlusPlus.Copilot.OpenRouter.Tests;

internal class FakeHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    public static FakeHttpMessageHandler ThatShouldNeverBeCalled() => new ThrowingHandler();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body) };
        return Task.FromResult(response);
    }

    private sealed class ThrowingHandler() : FakeHttpMessageHandler(HttpStatusCode.OK, "")
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No HTTP call should have been made.");
    }
}
