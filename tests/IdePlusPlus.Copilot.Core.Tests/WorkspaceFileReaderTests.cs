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
    public void ReadFile_ReturnsFileContentsPrefixedWithLineNumbers()
    {
        _reader.ReadFile(_root, "src/Foo.cs").ShouldBe("     1: class Foo { }");
    }

    [Fact]
    public void ReadFile_MultiLineFile_NumbersEveryLineSequentially()
    {
        File.WriteAllText(Path.Combine(_root, "multi.cs"), "one\ntwo\nthree");

        _reader.ReadFile(_root, "multi.cs").ShouldBe("     1: one\n     2: two\n     3: three");
    }

    [Fact]
    public void ReadFile_CrlfLineEndings_AreNumberedWithoutBlankLinesBetween()
    {
        File.WriteAllText(Path.Combine(_root, "crlf.cs"), "one\r\ntwo");

        _reader.ReadFile(_root, "crlf.cs").ShouldBe("     1: one\n     2: two");
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

        // The notice reports the real file size, not the length of the truncated copy.
        result.ShouldContain($"file is {WorkspaceFileReader.MaxFileChars + 500} chars");
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

    // --- Unsaved editor content ---

    /// <summary>An edit applied to an open file only touches the editor buffer, so disk goes stale
    /// the moment the first edit lands. Reading disk back would hand the model its own pre-edit
    /// content and make a successful edit look like it never happened.</summary>
    [Fact]
    public void ReadFile_WhenTheFileIsOpenWithUnsavedChanges_ShowsTheEditorsContent()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "STALE_DISK_CONTENT");
        var reader = new WorkspaceFileReader(_ => "in the editor");

        var result = reader.ReadFile(root, "Foo.cs");

        result.ShouldContain("in the editor");
        result.ShouldNotContain("STALE_DISK_CONTENT");
    }

    [Fact]
    public void ReadFile_WhenTheFileIsOpenWithUnsavedChanges_SaysWhichCopyItIs()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "on disk");

        new WorkspaceFileReader(_ => "in the editor")
            .ReadFile(root, "Foo.cs")
            .ShouldContain("unsaved changes");
    }

    [Fact]
    public void ReadFile_WhenNothingIsUnsaved_ReadsFromDiskAndSaysNothingExtra()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "on disk");

        var result = new WorkspaceFileReader(_ => null).ReadFile(root, "Foo.cs");

        result.ShouldContain("on disk");
        result.ShouldNotContain("unsaved changes");
    }

    [Fact]
    public void ReadFile_WithNoProviderAtAll_StillReadsFromDisk()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "on disk");

        new WorkspaceFileReader().ReadFile(root, "Foo.cs").ShouldContain("on disk");
    }

    /// <summary>A file that exists only in an editor buffer is still "not found" - the provider
    /// supplies content for a file, it does not conjure one into existence.</summary>
    [Fact]
    public void ReadFile_ForAFileThatIsNotOnDisk_IsStillNotFound()
    {
        var root = NewTempDirectory();

        new WorkspaceFileReader(_ => "in the editor")
            .ReadFile(root, "Missing.cs")
            .ShouldContain("File not found");
    }

    [Fact]
    public void ReadFile_UnsavedContentPastTheCap_IsTruncatedAndStillFlagged()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "small");
        var big = new string('x', WorkspaceFileReader.MaxFileChars + 500);

        var result = new WorkspaceFileReader(_ => big).ReadFile(root, "Foo.cs");

        result.ShouldContain("truncated");
        result.ShouldContain("unsaved changes");
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "IdePlusPlusWfrTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
