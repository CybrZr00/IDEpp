using System.Net;

namespace IdePlusPlus.NuGet.Tests;

/// <summary>Mirrors IdePlusPlus.Copilot.OpenRouter.Tests's FakeHttpMessageHandler - lets
/// NuGetSearchClient be unit-tested without a real network call to nuget.org.</summary>
internal class FakeHttpMessageHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
{
    public static FakeHttpMessageHandler ThatShouldNeverBeCalled() => new ThrowingHandler();

    /// <summary>Simulates a request that never completes in time - the real NuGetSearchClient
    /// would experience this as its own 8-second timeout firing, but throwing straight from the
    /// handler lets the test observe the same TaskCanceledException handling path instantly.</summary>
    public static FakeHttpMessageHandler ThatTimesOut() => new TimingOutHandler();

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

    private sealed class TimingOutHandler() : FakeHttpMessageHandler(HttpStatusCode.OK, "")
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new TaskCanceledException("Simulated timeout.");
    }
}
