using System.Diagnostics;
using Shouldly;
using Xunit;

namespace IdePlusPlus.TestRunner.Tests;

/// <summary>
/// Exercises TrxParser against a REAL .trx file produced by running `dotnet test --logger trx`
/// against a real scratch xunit project, rather than a hand-crafted fixture - the whole point of
/// this parser is to survive whatever VSTest actually emits, so that's what's worth verifying.
/// </summary>
public sealed class TrxParserTests : IDisposable
{
    private const string SourceCode = """
        using Xunit;

        namespace ScratchTests;

        public class SampleTests
        {
            [Fact]
            public void PassingTest()
            {
                Assert.True(true);
            }

            [Fact]
            public void FailingTest()
            {
                Assert.Fail("boom");
            }
        }
        """;

    private readonly ScratchXunitProject _project = new("TrxParserFixture", "SampleTests.cs", SourceCode);

    public void Dispose() => _project.Dispose();

    [Fact]
    public void Parse_OnARealTrxWithAPassingAndAFailingTest_ReturnsBothOutcomesCorrectly()
    {
        var trxPath = RunDotnetTestWithTrxLogger(_project);

        var results = TrxParser.Parse(trxPath);

        results.Count.ShouldBe(2);

        var passed = results.Single(r => r.Outcome == TestOutcome.Passed);
        passed.FullyQualifiedName.ShouldBe("ScratchTests.SampleTests.PassingTest");
        passed.ErrorMessage.ShouldBeNull();

        var failed = results.Single(r => r.Outcome == TestOutcome.Failed);
        failed.FullyQualifiedName.ShouldBe("ScratchTests.SampleTests.FailingTest");
        failed.ErrorMessage.ShouldNotBeNull();
        failed.ErrorMessage.ShouldContain("boom");
    }

    [Fact]
    public void Parse_ForATrxPathThatDoesNotExist_ReturnsEmptyListRatherThanThrowing()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.trx");

        var results = TrxParser.Parse(missingPath);

        results.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_ForSyntacticallyValidButUnexpectedlyShapedXml_ReturnsEmptyListRatherThanThrowing()
    {
        var emptyRootPath = Path.Combine(Path.GetTempPath(), $"unexpected-shape-{Guid.NewGuid():N}.trx");
        File.WriteAllText(emptyRootPath, "<TestRun></TestRun>");
        try
        {
            var results = TrxParser.Parse(emptyRootPath);

            results.ShouldBeEmpty();
        }
        finally
        {
            File.Delete(emptyRootPath);
        }
    }

    private static string RunDotnetTestWithTrxLogger(ScratchXunitProject project)
    {
        var resultsDirectory = Path.Combine(project.ProjectDirectory, "results");
        const string trxFileName = "result.trx";

        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = project.ProjectDirectory,
        };
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(project.CsprojPath);
        startInfo.ArgumentList.Add("--logger");
        startInfo.ArgumentList.Add($"trx;LogFileName={trxFileName}");
        startInfo.ArgumentList.Add("--results-directory");
        startInfo.ArgumentList.Add(resultsDirectory);

        using var process = Process.Start(startInfo)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        // Intentionally not asserting on ExitCode - `dotnet test` exits non-zero when any test
        // fails, which is exactly what this fixture wants to happen so it can produce a Failed
        // <UnitTestResult> to parse.
        process.WaitForExit();

        var trxPath = Path.Combine(resultsDirectory, trxFileName);
        File.Exists(trxPath).ShouldBeTrue("dotnet test --logger trx should have produced a .trx file.");
        return trxPath;
    }
}
