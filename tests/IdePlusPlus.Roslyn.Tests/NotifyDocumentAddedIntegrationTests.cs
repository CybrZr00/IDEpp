using IdePlusPlus.Roslyn;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// Against the same real scratch solution as the other integration tests: a file created after the
/// solution was loaded becomes analysable without reloading it.
///
/// <para>This is the gap New Item left behind. An SDK-style project globs its sources, so a new .cs
/// file is a project item the moment it exists - but the workspace only learned what a project
/// contained at load time, so the file compiled and had no IntelliSense until the solution was
/// reopened.</para>
/// </summary>
[Collection(nameof(RoslynScratchSolutionCollection))]
public sealed class NotifyDocumentAddedIntegrationTests
{
    private readonly RoslynScratchSolutionFixture _fixture;

    public NotifyDocumentAddedIntegrationTests(RoslynScratchSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task AFileAddedAfterLoadBecomesAnalysed()
    {
        var path = await WriteScratchFileAsync("AddedAfterLoad", """
            namespace ScratchLib;

            public class AddedAfterLoad
            {
            }
            """);

        _fixture.WorkspaceService.IsAnalysed(path).ShouldBeFalse();

        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(path, _fixture.ProjectPath)).ShouldBeTrue();

        _fixture.WorkspaceService.IsAnalysed(path).ShouldBeTrue();
    }

    /// <summary>The point of the exercise: a real semantic error in a just-added file, from the same
    /// semantic model the Error List reads.</summary>
    [Fact]
    public async Task ARealDiagnosticIsReportedForAJustAddedFile()
    {
        var path = await WriteScratchFileAsync("AddedWithError", """
            namespace ScratchLib;

            public class AddedWithError
            {
                public void Broken()
                {
                    string s = 5;
                }
            }
            """);

        await _fixture.WorkspaceService.NotifyDocumentAddedAsync(path, _fixture.ProjectPath);

        var diagnostics = await _fixture.WorkspaceService.GetDiagnosticsAsync(path);

        diagnostics.ShouldContain(d => d.Message.Contains("int", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A folder that didn't exist at load time - the New Item case that motivated all of
    /// this.</summary>
    [Fact]
    public async Task AFileInANewSubfolderIsAnalysedToo()
    {
        var directory = Path.Combine(_fixture.Directory, "AddedFolder", "Nested");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Nested.cs");
        await File.WriteAllTextAsync(path, """
            namespace ScratchLib.AddedFolder.Nested;

            public class Nested
            {
            }
            """);

        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(path, _fixture.ProjectPath)).ShouldBeTrue();
        (await _fixture.WorkspaceService.GetDiagnosticsAsync(path)).ShouldBeEmpty();
    }

    /// <summary>Adding twice is the caller's goal already satisfied, not a failure - a reload can
    /// race a New Item.</summary>
    [Fact]
    public async Task AddingTheSameFileTwiceIsNotAFailure()
    {
        var path = await WriteScratchFileAsync("AddedTwice", "namespace ScratchLib; public class AddedTwice { }");

        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(path, _fixture.ProjectPath)).ShouldBeTrue();
        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(path, _fixture.ProjectPath)).ShouldBeTrue();
    }

    [Fact]
    public async Task AProjectThisWorkspaceNeverLoadedIsRefused()
    {
        var path = await WriteScratchFileAsync("WrongProject", "namespace ScratchLib; public class WrongProject { }");

        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(
            path, Path.Combine(_fixture.Directory, "NotAProject.csproj"))).ShouldBeFalse();
    }

    /// <summary>.editorconfig, .json and friends are additional documents, which nothing here reads -
    /// claiming to have added one would promise analysis that never arrives.</summary>
    [Fact]
    public async Task ANonSourceFileIsRefused()
    {
        var path = Path.Combine(_fixture.Directory, "added.json");
        await File.WriteAllTextAsync(path, "{}");

        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(path, _fixture.ProjectPath)).ShouldBeFalse();
    }

    [Fact]
    public async Task AFileThatIsNotOnDiskIsRefused() =>
        (await _fixture.WorkspaceService.NotifyDocumentAddedAsync(
            Path.Combine(_fixture.Directory, $"missing-{Guid.NewGuid():N}.cs"), _fixture.ProjectPath))
            .ShouldBeFalse();

    private async Task<string> WriteScratchFileAsync(string name, string source)
    {
        var path = Path.Combine(_fixture.Directory, $"{name}.cs");
        await File.WriteAllTextAsync(path, source);
        return path;
    }
}
