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

    // --- Paging ---

    /// <summary>Without paging, a file past the character cap simply could not be read to the end -
    /// leaving the model to guess at the tail, which is the one thing it must not do before an edit.</summary>
    [Fact]
    public void ReadFile_WithAnOffset_StartsThereAndNumbersFromThere()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "a\nb\nc\nd\ne");

        var result = new WorkspaceFileReader().ReadFile(root, "Foo.cs", offset: 3);

        result.ShouldContain("     3: c");
        result.ShouldNotContain(": a");
        result.ShouldContain("showing lines 3-5 of 5");
    }

    [Fact]
    public void ReadFile_WithALimit_StopsAfterThatManyLines()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "a\nb\nc\nd\ne");

        var result = new WorkspaceFileReader().ReadFile(root, "Foo.cs", limit: 2);

        result.ShouldContain("     1: a");
        result.ShouldContain("     2: b");
        result.ShouldNotContain(": c");
        result.ShouldContain("Read on with offset: 3");
    }

    [Fact]
    public void ReadFile_WithOffsetAndLimitTogether_ReturnsThatWindow()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "a\nb\nc\nd\ne");

        var result = new WorkspaceFileReader().ReadFile(root, "Foo.cs", offset: 2, limit: 2);

        result.ShouldContain("     2: b");
        result.ShouldContain("     3: c");
        result.ShouldNotContain(": d");
    }

    [Fact]
    public void ReadFile_ReadingToTheEnd_DoesNotClaimToBeTruncated()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "a\nb\nc");

        new WorkspaceFileReader().ReadFile(root, "Foo.cs", offset: 2).ShouldNotContain("truncated");
    }

    [Fact]
    public void ReadFile_WithAnOffsetPastTheEnd_SaysHowLongTheFileIs()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "Foo.cs"), "a\nb\nc");

        new WorkspaceFileReader().ReadFile(root, "Foo.cs", offset: 99).ShouldContain("has 3 line(s)");
    }

    /// <summary>The hint has to be a real line number, or following it skips or repeats content.</summary>
    [Fact]
    public void ReadFile_TheOfferedOffset_ResumesExactlyWhereItLeftOff()
    {
        var root = NewTempDirectory();
        var lines = Enumerable.Range(1, 4000).Select(i => new string('x', 40) + i).ToArray();
        File.WriteAllText(Path.Combine(root, "Big.cs"), string.Join("\n", lines));
        var reader = new WorkspaceFileReader();

        var first = reader.ReadFile(root, "Big.cs");
        var match = System.Text.RegularExpressions.Regex.Match(first, @"Read on with offset: (\d+)");
        match.Success.ShouldBeTrue();

        var next = int.Parse(match.Groups[1].Value);
        first.ShouldContain($"{next - 1,6}: {lines[next - 2]}");
        reader.ReadFile(root, "Big.cs", offset: next).ShouldContain($"{next,6}: {lines[next - 1]}");
    }

    /// <summary>A cut never lands mid-line, so a page always ends on a line the model can trust.</summary>
    [Fact]
    public void ReadFile_WhenCappedByLength_CutsOnALineBoundary()
    {
        var root = NewTempDirectory();
        var line = new string('x', 1000);
        File.WriteAllText(Path.Combine(root, "Big.cs"), string.Join("\n", Enumerable.Repeat(line, 200)));

        var result = new WorkspaceFileReader().ReadFile(root, "Big.cs");

        result.ShouldContain("truncated");
        foreach (var rendered in result.Split('\n').Where(l => l.Contains(": x")))
        {
            rendered.TrimEnd().Length.ShouldBe(8 + 1000);
        }
    }

    /// <summary>A minified bundle on one line would otherwise return nothing at all.</summary>
    [Fact]
    public void ReadFile_WithASingleLineLongerThanTheBudget_CutsItAndSaysSo()
    {
        var root = NewTempDirectory();
        File.WriteAllText(Path.Combine(root, "bundle.js"), new string('x', WorkspaceFileReader.MaxFileChars + 5000));

        var result = new WorkspaceFileReader().ReadFile(root, "bundle.js");

        result.ShouldContain("unusually long");
        result.Length.ShouldBeLessThan(WorkspaceFileReader.MaxFileChars + 200);
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
