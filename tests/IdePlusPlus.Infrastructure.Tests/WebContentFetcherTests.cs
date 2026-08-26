using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

/// <summary>
/// Deliberately does not hit the live network - a test suite that depends on some other host being
/// reachable is not one anyone can trust, and every behaviour worth pinning down here (scheme
/// validation, the SSRF block, DNS failure) doesn't need a real response to prove. WebHostGuardTests
/// covers the address-range logic directly; this covers FetchAsync actually consulting it before
/// ever opening a connection - "localhost", loopback/metadata literals, and an RFC 2606 .invalid
/// hostname all resolve (or fail to) without needing real connectivity.
/// </summary>
public class WebContentFetcherTests
{
    private readonly WebContentFetcher _fetcher = new();

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    public async Task FetchAsync_ForANonHttpUrl_FailsWithoutAnyNetworkAccess(string url)
    {
        var result = await _fetcher.FetchAsync(url, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Text.ShouldContain("not a valid http");
    }

    [Theory]
    [InlineData("http://127.0.0.1:1/")]
    [InlineData("http://localhost:1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://[::1]:1/")]
    public async Task FetchAsync_ForALocalOrPrivateAddress_IsRefusedUnconditionally(string url)
    {
        var result = await _fetcher.FetchAsync(url, CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Text.ShouldContain("Refused");
    }

    [Fact]
    public async Task FetchAsync_ForAHostThatDoesNotResolve_FailsCleanly()
    {
        var result = await _fetcher.FetchAsync("http://this-host-should-never-resolve.invalid/", CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Text.ShouldContain("resolve");
    }
}
