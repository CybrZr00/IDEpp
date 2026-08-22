using Shouldly;
using Xunit;

namespace IdePlusPlus.Run.Tests;

public class KestrelUrlDetectorTests
{
    [Theory]
    [InlineData("      Now listening on: https://localhost:7071", "https://localhost:7071")]
    [InlineData("info: Microsoft.Hosting.Lifetime[14]", null)]
    [InlineData("      Now listening on: http://localhost:5000", "http://localhost:5000")]
    [InlineData("", null)]
    [InlineData("Application started. Press Ctrl+C to shut down.", null)]
    public void TryExtractListeningUrl_MatchesOnlyTheKestrelStartupLine(string line, string? expected)
    {
        KestrelUrlDetector.TryExtractListeningUrl(line).ShouldBe(expected);
    }
}
