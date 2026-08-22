using System.Net;
using Shouldly;
using Xunit;

namespace IdePlusPlus.NuGet.Tests;

/// <summary>
/// Unit tests for NuGetSearchClient against a fake HttpMessageHandler - nuget.org itself is never
/// hit here (that would make the test suite depend on the network and the live search index).
/// </summary>
public class NuGetSearchClientTests
{
    [Fact]
    public async Task SearchAsync_OnSuccessfulResponse_ParsesResultsIntoRecords()
    {
        const string body = """
            {
              "totalHits": 2,
              "data": [
                {
                  "id": "Newtonsoft.Json",
                  "version": "13.0.3",
                  "totalDownloads": 3000000000,
                  "description": "Json.NET is a popular high-performance JSON framework for .NET"
                },
                {
                  "id": "Newtonsoft.Json.Bson",
                  "version": "1.0.3",
                  "totalDownloads": 123456789,
                  "description": "Bson support for Json.NET"
                }
              ]
            }
            """;
        var client = new NuGetSearchClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, body)));

        var (results, error) = await client.SearchAsync("newtonsoft.json");

        error.ShouldBeNull();
        results.Count.ShouldBe(2);
        results[0].ShouldBe(new NuGetSearchResult("Newtonsoft.Json", "13.0.3", 3000000000, "Json.NET is a popular high-performance JSON framework for .NET"));
        results[1].Id.ShouldBe("Newtonsoft.Json.Bson");
        results[1].TotalDownloads.ShouldBe(123456789);
    }

    [Fact]
    public async Task SearchAsync_OnHttpErrorStatus_ReturnsErrorAndEmptyResultsWithoutThrowing()
    {
        var client = new NuGetSearchClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.InternalServerError, "boom")));

        var (results, error) = await client.SearchAsync("newtonsoft.json");

        results.ShouldBeEmpty();
        error.ShouldNotBeNull();
        error.ShouldContain("500");
    }

    [Fact]
    public async Task SearchAsync_WhenTheRequestTimesOut_ReturnsErrorWithoutThrowingOrHanging()
    {
        // The real client waits up to 8 real seconds before its own timeout fires; simulating the
        // TaskCanceledException directly from the handler exercises the same catch branch without
        // making this test slow.
        var client = new NuGetSearchClient(new HttpClient(FakeHttpMessageHandler.ThatTimesOut()));

        var (results, error) = await client.SearchAsync("newtonsoft.json");

        results.ShouldBeEmpty();
        error.ShouldNotBeNull();
        error.ShouldContain("timed out");
    }

    [Fact]
    public async Task SearchAsync_OnMalformedJson_ReturnsErrorAndEmptyResultsWithoutThrowing()
    {
        var client = new NuGetSearchClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, "not json at all")));

        var (results, error) = await client.SearchAsync("newtonsoft.json");

        results.ShouldBeEmpty();
        error.ShouldNotBeNull();
    }

    [Fact]
    public async Task SearchAsync_WithNoDataField_ReturnsEmptyResultsWithNoError()
    {
        var client = new NuGetSearchClient(new HttpClient(new FakeHttpMessageHandler(HttpStatusCode.OK, """{"totalHits": 0}""")));

        var (results, error) = await client.SearchAsync("some-package-that-does-not-exist");

        results.ShouldBeEmpty();
        error.ShouldBeNull();
    }
}
