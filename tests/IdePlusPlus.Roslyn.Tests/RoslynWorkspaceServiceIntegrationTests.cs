using System.Linq;
using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// The single most important test in this feature: proves - against a real scratch solution built
/// by the actual `dotnet` CLI and loaded through a real <see cref="RoslynWorkspaceService"/> backed
/// by a real <c>MSBuildWorkspace</c> - that pushing a genuinely broken edit through
/// <see cref="RoslynWorkspaceService.NotifyDocumentChanged"/> produces a real, correct
/// <see cref="LiveDiagnostic"/>. This is exactly the code path that would surface a
/// Microsoft.CodeAnalysis.* vs MSBuildLocator-resolved-MSBuild version mismatch as a
/// MissingMethodException/FileLoadException at runtime - a mock can't catch that, only a real
/// end-to-end run can.
/// </summary>
[Collection(nameof(RoslynScratchSolutionCollection))]
public sealed class RoslynWorkspaceServiceIntegrationTests
{
    private readonly RoslynScratchSolutionFixture _fixture;

    public RoslynWorkspaceServiceIntegrationTests(RoslynScratchSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task NotifyDocumentChanged_WithARealCompileError_FiresDiagnosticsChangedWithARealDiagnostic()
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<LiveDiagnostic>>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(string path, IReadOnlyList<LiveDiagnostic> diagnostics)
        {
            if (string.Equals(path, _fixture.ClassFilePath, StringComparison.OrdinalIgnoreCase))
            {
                tcs.TrySetResult(diagnostics);
            }
        }

        _fixture.WorkspaceService.DiagnosticsChanged += Handler;
        try
        {
            // A real semantic error (CS0029: can't implicitly convert int to string) - not a
            // syntax error - so this also proves the semantic model (not just the parser) is wired
            // up correctly end to end.
            const string brokenSource = """
                namespace ScratchLib;

                public class Class1
                {
                    public void Broken()
                    {
                        string s = 5;
                    }
                }

                """;

            _fixture.WorkspaceService.NotifyDocumentChanged(_fixture.ClassFilePath, brokenSource);

            // Debounce is 300ms; give real MSBuildWorkspace/semantic-model computation generous
            // real wall-clock room on top of that rather than rushing/weakening the assertion.
            var diagnostics = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60));

            diagnostics.ShouldNotBeEmpty();

            // Filtered by code rather than asserting the list has exactly one element: the point is
            // that the real CS0029 we introduced is genuinely present and correctly shaped, not that
            // nothing else (e.g. an unrelated analyzer diagnostic) could ever also appear.
            var conversionError = diagnostics.Single(d => d.Code == "CS0029");
            conversionError.Severity.ShouldBe("Error");
            conversionError.FilePath.ShouldBe(_fixture.ClassFilePath);
            conversionError.Line.ShouldBeGreaterThan(0);
            conversionError.Column.ShouldBeGreaterThan(0);
            conversionError.EndLine.ShouldBeGreaterThanOrEqualTo(conversionError.Line);
            conversionError.Message.ShouldContain("string");
        }
        finally
        {
            _fixture.WorkspaceService.DiagnosticsChanged -= Handler;
        }
    }

    // --- GetDiagnosticsAsync: the pull API behind the copilot's get_diagnostics tool ---

    /// <summary>
    /// The push path announces diagnostics after a debounce, which suits a pane watching the screen
    /// and not a caller that wants an answer. Same semantic model, asked directly.
    /// </summary>
    [Fact]
    public async Task GetDiagnosticsAsync_WithUnsavedTextContainingARealError_ReturnsIt()
    {
        const string broken = """
            namespace ScratchLib;
            public class Class1
            {
                public void M() { string s = 1; }
            }
            """;

        var diagnostics = await _fixture.WorkspaceService.GetDiagnosticsAsync(_fixture.ClassFilePath, broken);

        diagnostics.ShouldContain(d => d.Code == "CS0029" && d.Severity == "Error");
    }

    [Fact]
    public async Task GetDiagnosticsAsync_ReportsOneBasedLineAndColumn()
    {
        const string broken = """
            namespace ScratchLib;
            public class Class1
            {
                public void M() { string s = 1; }
            }
            """;

        var diagnostics = await _fixture.WorkspaceService.GetDiagnosticsAsync(_fixture.ClassFilePath, broken);

        var error = diagnostics.First(d => d.Code == "CS0029");
        error.Line.ShouldBe(4);
        error.Column.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task GetDiagnosticsAsync_WithValidUnsavedText_ReturnsNoErrors()
    {
        const string valid = """
            namespace ScratchLib;
            public class Class1
            {
                public void M() { string s = "ok"; System.Console.WriteLine(s); }
            }
            """;

        var diagnostics = await _fixture.WorkspaceService.GetDiagnosticsAsync(_fixture.ClassFilePath, valid);

        diagnostics.ShouldNotContain(d => d.Severity == "Error");
    }

    /// <summary>A file the workspace never loaded must not come back as a clean bill of health -
    /// the caller needs to be able to say "not analysed" instead.</summary>
    [Fact]
    public async Task GetDiagnosticsAsync_ForAFileTheWorkspaceNeverLoaded_ReturnsEmpty()
    {
        var path = Path.Combine(_fixture.Directory, "NotInTheSolution.cs");

        (await _fixture.WorkspaceService.GetDiagnosticsAsync(path, "class Nope { int x = ; }")).ShouldBeEmpty();
    }

    [Fact]
    public void IsAnalysed_DistinguishesLoadedFilesFromEverythingElse()
    {
        _fixture.WorkspaceService.IsAnalysed(_fixture.ClassFilePath).ShouldBeTrue();
        _fixture.WorkspaceService
            .IsAnalysed(Path.Combine(_fixture.Directory, "NotInTheSolution.cs"))
            .ShouldBeFalse();
    }

    /// <summary>Omitting the text argument reads whatever the workspace currently holds, rather
    /// than requiring the caller to supply a buffer it may not have.</summary>
    [Fact]
    public async Task GetDiagnosticsAsync_WithoutUnsavedText_AnalysesTheCurrentSnapshot()
    {
        const string valid = """
            namespace ScratchLib;
            public class Class1
            {
                public void M() { System.Console.WriteLine("ok"); }
            }
            """;
        await _fixture.WorkspaceService.GetDiagnosticsAsync(_fixture.ClassFilePath, valid);

        var diagnostics = await _fixture.WorkspaceService.GetDiagnosticsAsync(_fixture.ClassFilePath);

        diagnostics.ShouldNotContain(d => d.Severity == "Error");
    }
}
