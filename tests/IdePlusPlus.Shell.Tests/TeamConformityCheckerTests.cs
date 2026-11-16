using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// check_conformity drives the SDK directly, so these tests script `dotnet` rather than spawn it:
/// what matters is which commands run, in which order, over which files, and whether the report
/// only keeps the diagnostics for the files under review.
/// </summary>
public sealed class TeamConformityCheckerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-conformity-{Guid.NewGuid():N}");
    private readonly string _solution;

    public TeamConformityCheckerTests()
    {
        _solution = Path.Combine(_root, "Scratch.slnx");
        Directory.CreateDirectory(Path.Combine(_root, "Lib"));
        File.WriteAllText(_solution, "<Solution />");
        File.WriteAllText(Path.Combine(_root, "Lib", "Class1.cs"), "public class Class1 { }\n");
        File.WriteAllText(Path.Combine(_root, "Lib", "Class2.cs"), "public class Class2 { }\n");
        File.WriteAllText(Path.Combine(_root, "Other.cs"), "public class Other { }\n");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort - a locked temp file must not fail the test.
        }
    }

    private string Path1 => Path.Combine(_root, "Lib", "Class1.cs");

    private TeamConformityChecker Checker(ScriptedDotnet dotnet, TimeSpan? stepTimeout = null,
        Func<string, CancellationToken, Task<(bool IsError, string Text)>>? diagnostics = null) =>
        new(_root, _solution, "Debug", dotnet.RunAsync, diagnostics, stepTimeout);

    [Fact]
    public async Task Check_RunsStyleThenAnalyzersThenBuild_OverTheNamedFiles()
    {
        var dotnet = new ScriptedDotnet();

        var report = await Checker(dotnet).CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        report.Refused.ShouldBeFalse();
        report.Clean.ShouldBeTrue(report.Text);
        dotnet.Calls.Count.ShouldBe(3);
        dotnet.Calls[0].Arguments.ShouldBe(["format", _solution, "style", "--include", Path1]);
        dotnet.Calls[1].Arguments.ShouldBe(["format", _solution, "analyzers", "--verify-no-changes", "--include", Path1]);
        dotnet.Calls[2].Arguments.Take(2).ShouldBe(["build", _solution]);
        dotnet.Calls.ShouldAllBe(call => call.WorkingDirectory == _root);
        report.Files.ShouldHaveSingleItem().ShouldContain("Class1.cs");
        report.Text.ShouldContain("Verdict: conforming");
    }

    [Fact]
    public async Task Check_ReportsWhatTheAnalyzerStillFinds_AndPointsAtAFinding()
    {
        var dotnet = new ScriptedDotnet();
        dotnet.Respond = (arguments, _) => Task.FromResult(arguments[2] switch
        {
            "style" => new DotnetRunResult(0, "Formatted Lib/Class1.cs"),
            "analyzers" => new DotnetRunResult(
                2, "Lib/Class1.cs(3,5): warning IDE0005: Using directive is unnecessary.\n"),
            _ => new DotnetRunResult(0, ""),
        });

        var report = await Checker(dotnet).CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        report.Clean.ShouldBeFalse();
        report.AnalyzerProblems.ShouldHaveSingleItem().ShouldContain("IDE0005");
        report.Text.ShouldContain("IDE0005");
        report.Text.ShouldContain("FINDING");
        report.Text.ShouldContain("severity=");
    }

    [Fact]
    public async Task Check_KeepsOnlyTheBuildDiagnosticsForTheFilesUnderReview()
    {
        var dotnet = new ScriptedDotnet();
        dotnet.Respond = (arguments, _) => Task.FromResult(arguments[0] == "build"
            ? new DotnetRunResult(
                0,
                $"{Path1}(3,5): warning CS0168: The variable 'x' is declared but never used [Lib]"
                + $"\n{Path.Combine(_root, "Other.cs")}(1,1): warning CS0219: Unrelated [Other]")
            : new DotnetRunResult(0, ""));

        var report = await Checker(dotnet).CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        report.BuildWarnings.ShouldHaveSingleItem().ShouldContain("CS0168");
        report.Text.ShouldNotContain("CS0219");
        report.Clean.ShouldBeFalse();
    }

    [Fact]
    public async Task Check_ReportsAFailedBuildEvenWhenNoDiagnosticNamesTheFile()
    {
        var dotnet = new ScriptedDotnet();
        dotnet.Respond = (arguments, _) => Task.FromResult(arguments[0] == "build"
            ? new DotnetRunResult(1, "error MSB1009: Project file does not exist.")
            : new DotnetRunResult(0, ""));

        var report = await Checker(dotnet).CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        report.Clean.ShouldBeFalse();
        report.BuildWarnings.ShouldHaveSingleItem().ShouldContain("build failed");
        report.Text.ShouldContain("FAILED");
    }

    [Theory]
    [InlineData("../Other.cs")]
    [InlineData("Lib/Missing.cs")]
    [InlineData("")]
    public async Task Check_RefusesAFileItCannotCheck_WithoutRunningDotnet(string file)
    {
        var dotnet = new ScriptedDotnet();

        var report = await Checker(dotnet).CheckAsync([file], CancellationToken.None);

        report.Refused.ShouldBeTrue();
        report.Clean.ShouldBeFalse();
        dotnet.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_RefusesAnEmptyFileList()
    {
        var dotnet = new ScriptedDotnet();

        var report = await Checker(dotnet).CheckAsync([], CancellationToken.None);

        report.Refused.ShouldBeTrue();
        report.Text.ShouldContain("at least one file");
        dotnet.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Check_IncludesGetDiagnosticsForTheReviewedFiles()
    {
        var asked = new List<string>();
        var dotnet = new ScriptedDotnet();

        var report = await Checker(
            dotnet,
            diagnostics: (file, _) =>
            {
                asked.Add(file);
                return Task.FromResult((false, $"{file}: field 'cache' is never read"));
            }).CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        asked.ShouldHaveSingleItem().ShouldContain("Class1.cs");
        report.Diagnostics.ShouldHaveSingleItem().ShouldContain("never read");
        report.Text.ShouldContain("never read");
    }

    [Fact]
    public async Task Check_StopsAStepThatHangs_AndSaysSo()
    {
        var dotnet = new ScriptedDotnet
        {
            Respond = async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return new DotnetRunResult(0, "");
            },
        };

        var report = await Checker(dotnet, TimeSpan.FromMilliseconds(100))
            .CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        report.Clean.ShouldBeFalse();
        report.Text.ShouldContain("still running after");
    }

    [Fact]
    public async Task Check_SaysWhenDotnetItselfCouldNotRun()
    {
        var dotnet = new ScriptedDotnet
        {
            Respond = (_, _) => Task.FromResult(new DotnetRunResult(
                -1, "Could not run dotnet - is the SDK installed and on PATH?", Started: false)),
        };

        var report = await Checker(dotnet).CheckAsync(["Lib/Class1.cs"], CancellationToken.None);

        report.Refused.ShouldBeFalse();
        report.Clean.ShouldBeFalse();
        report.Text.ShouldContain("could not run `dotnet`");
        dotnet.Calls.Count.ShouldBe(1);
    }

    private sealed class ScriptedDotnet
    {
        public List<(IReadOnlyList<string> Arguments, string WorkingDirectory)> Calls { get; } = [];

        public Func<IReadOnlyList<string>, CancellationToken, Task<DotnetRunResult>> Respond { get; set; } =
            (_, _) => Task.FromResult(new DotnetRunResult(0, ""));

        public Task<DotnetRunResult> RunAsync(
            IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
        {
            Calls.Add((arguments, workingDirectory));
            return Respond(arguments, cancellationToken);
        }
    }
}
