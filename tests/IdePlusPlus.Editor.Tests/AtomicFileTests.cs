using System.IO;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"idepp-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var file in Directory.GetFiles(_dir))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_dir, recursive: true);
    }

    private string PathOf(string name) => Path.Combine(_dir, name);

    [Fact]
    public void WritesANewFile()
    {
        AtomicFile.WriteAllText(PathOf("New.cs"), "hello");

        File.ReadAllText(PathOf("New.cs")).ShouldBe("hello");
        Directory.GetFiles(_dir).Length.ShouldBe(1);
    }

    [Fact]
    public void ReplacesAnExistingFile_AndLeavesNoTempFileBehind()
    {
        File.WriteAllText(PathOf("A.cs"), "old content that is longer than the new");

        AtomicFile.WriteAllText(PathOf("A.cs"), "new");

        File.ReadAllText(PathOf("A.cs")).ShouldBe("new");
        Directory.GetFiles(_dir).Select(Path.GetFileName).ShouldBe(["A.cs"]);
    }

    [Fact]
    public void WritesTheSameBytesAsFileWriteAllText()
    {
        const string text = "café → line1\r\nline2\n";
        File.WriteAllText(PathOf("Expected.txt"), text);

        AtomicFile.WriteAllText(PathOf("Actual.txt"), text);

        File.ReadAllBytes(PathOf("Actual.txt")).ShouldBe(File.ReadAllBytes(PathOf("Expected.txt")));
    }

    [Fact]
    public void EmptyContent_ProducesAnEmptyFile()
    {
        File.WriteAllText(PathOf("A.cs"), "something");

        AtomicFile.WriteAllText(PathOf("A.cs"), "");

        new FileInfo(PathOf("A.cs")).Length.ShouldBe(0);
    }

    [Fact]
    public void AFailedReplace_LeavesTheOriginalIntact_AndCleansUpItsTempFile()
    {
        File.WriteAllText(PathOf("A.cs"), "original");
        using var locked = new FileStream(PathOf("A.cs"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Should.Throw<IOException>(() => AtomicFile.WriteAllText(PathOf("A.cs"), "replacement"));

        locked.Dispose();
        File.ReadAllText(PathOf("A.cs")).ShouldBe("original");
        Directory.GetFiles(_dir).Select(Path.GetFileName).ShouldBe(["A.cs"]);
    }

    [Fact]
    public void AReadOnlyTarget_IsRefused_AndLeftIntact()
    {
        File.WriteAllText(PathOf("A.cs"), "original");
        File.SetAttributes(PathOf("A.cs"), FileAttributes.ReadOnly);

        Should.Throw<UnauthorizedAccessException>(() => AtomicFile.WriteAllText(PathOf("A.cs"), "replacement"));

        File.ReadAllText(PathOf("A.cs")).ShouldBe("original");
        Directory.GetFiles(_dir).Select(Path.GetFileName).ShouldBe(["A.cs"]);
    }

    [Fact]
    public void KeepsTheTargetsAttributes()
    {
        File.WriteAllText(PathOf("A.cs"), "original");
        File.SetAttributes(PathOf("A.cs"), FileAttributes.Hidden);

        AtomicFile.WriteAllText(PathOf("A.cs"), "new");

        File.GetAttributes(PathOf("A.cs")).HasFlag(FileAttributes.Hidden).ShouldBeTrue();
    }

    [Fact]
    public void MissingDirectory_Throws_RatherThanCreatingIt()
    {
        Should.Throw<DirectoryNotFoundException>(() => AtomicFile.WriteAllText(PathOf("nope/A.cs"), "x"));
    }
}
