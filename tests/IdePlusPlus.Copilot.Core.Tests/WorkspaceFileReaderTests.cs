using IdePlusPlus.Copilot.Core;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public sealed class WorkspaceFileReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-ws-{Guid.NewGuid()}");
    private readonly WorkspaceFileReader _reader = new();

    public WorkspaceFileReaderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Sub"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        File.WriteAllText(Path.Combine(_root, "README.md"), "# readme");
        File.WriteAllText(Path.Combine(_root, "src", "Foo.cs"), "class Foo { }");
        File.WriteAllText(Path.Combine(_root, "src", "Sub", "Deep.cs"), "class Deep { }");
        File.WriteAllText(Path.Combine(_root, "bin", "ignored.dll"), "binary");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ReadFile_ReturnsFileContents()
    {
        _reader.ReadFile(_root, "src/Foo.cs").ShouldBe("class Foo { }");
    }

    [Fact]
    public void ReadFile_MissingFile_ReturnsNotFoundMessageRatherThanThrowing()
    {
        _reader.ReadFile(_root, "src/Nope.cs").ShouldContain("File not found");
    }

    [Fact]
    public void ReadFile_NoSolutionOpen_ExplainsRatherThanThrowing()
    {
        _reader.ReadFile(null, "anything.cs").ShouldContain("No solution is open");
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../outside.txt")]
    [InlineData("src/../../outside.txt")]
    public void ReadFile_PathEscapingTheSolutionRoot_IsRefused(string escapingPath)
    {
        var result = _reader.ReadFile(_root, escapingPath);

        result.ShouldContain("Refused");
        result.ShouldContain("outside the open solution");
    }

    [Fact]
    public void ReadFile_AbsolutePathOutsideRoot_IsRefused()
    {
        var outside = Path.Combine(Path.GetTempPath(), "definitely-outside.txt");

        _reader.ReadFile(_root, outside).ShouldContain("Refused");
    }

    [Fact]
    public void ReadFile_OversizedFile_IsTruncatedWithNotice()
    {
        var bigPath = Path.Combine(_root, "big.txt");
        File.WriteAllText(bigPath, new string('x', WorkspaceFileReader.MaxFileChars + 500));

        var result = _reader.ReadFile(_root, "big.txt");

        result.ShouldContain("[truncated");
        result.Length.ShouldBeLessThan(WorkspaceFileReader.MaxFileChars + 200);
    }

    [Fact]
    public void ListFiles_RootNonRecursive_ListsTopLevelAndExcludesBuildOutput()
    {
        var result = _reader.ListFiles(_root, null, recursive: false);

        result.ShouldContain("README.md");
        result.ShouldContain("src");
        result.ShouldNotContain("bin");
        result.ShouldNotContain("Foo.cs");
    }

    [Fact]
    public void ListFiles_Recursive_IncludesNestedFiles()
    {
        var result = _reader.ListFiles(_root, null, recursive: true);

        result.ShouldContain("src/Foo.cs");
        result.ShouldContain("src/Sub/Deep.cs");
    }

    [Fact]
    public void ListFiles_Recursive_StillExcludesBuildOutput()
    {
        _reader.ListFiles(_root, null, recursive: true).ShouldNotContain("ignored.dll");
    }

    [Fact]
    public void ListFiles_Subdirectory_ListsOnlyThatDirectory()
    {
        var result = _reader.ListFiles(_root, "src", recursive: false);

        result.ShouldContain("src/Foo.cs");
        result.ShouldNotContain("README.md");
    }

    [Fact]
    public void ListFiles_EscapingPath_IsRefused()
    {
        _reader.ListFiles(_root, "../", recursive: false).ShouldContain("Refused");
    }

    [Fact]
    public void ResolveWithinRoot_SiblingDirectorySharingANamePrefix_IsRefused()
    {
        // "C:\repo\SolutionBackup" must not count as inside "C:\repo\Solution".
        var root = Path.Combine(Path.GetTempPath(), "Solution");

        WorkspaceFileReader.ResolveWithinRoot(root, "../SolutionBackup/secret.txt").ShouldBeNull();
    }

    [Fact]
    public void ResolveWithinRoot_TheRootItself_IsAllowed()
    {
        WorkspaceFileReader.ResolveWithinRoot(_root, ".").ShouldNotBeNull();
    }
}
