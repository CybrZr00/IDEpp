using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Diff.Tests;

/// <summary>The model-facing save. What matters is what it refuses to touch and that a failed write
/// does not lose the change.</summary>
public sealed class SaveWithinWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"idepp-save-{Guid.NewGuid():N}");
    private readonly string _outside = Path.Combine(Path.GetTempPath(), $"idepp-save-outside-{Guid.NewGuid():N}");
    private readonly EditorWorkspace _workspace;
    private readonly EditApplicationService _service;

    public SaveWithinWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _workspace = new EditorWorkspace(Path.Combine(_root, "recent.json"));
        _service = new EditApplicationService(_workspace);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    private DocumentViewModel OpenDirty(string directory, string name, string disk, string buffer)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, disk);
        var document = _workspace.Open(path)!;
        document.TextDocument.Text = buffer;
        document.IsDirty.ShouldBeTrue();
        return document;
    }

    [Fact]
    public void NoPaths_SavesEveryDirtyDocumentInsideTheSolution()
    {
        var a = OpenDirty(_root, "A.cs", "old", "new A");
        var b = OpenDirty(_root, "B.cs", "old", "new B");

        var report = _service.SaveWithinWorkspace(null, _root);

        report.Saved.Count.ShouldBe(2);
        File.ReadAllText(a.FilePath!).ShouldBe("new A");
        File.ReadAllText(b.FilePath!).ShouldBe("new B");
        a.IsDirty.ShouldBeFalse();
        b.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void NoPaths_LeavesADirtyDocumentOutsideTheSolutionUnsaved()
    {
        var inside = OpenDirty(_root, "A.cs", "old", "new A");
        var outside = OpenDirty(_outside, "Elsewhere.cs", "old", "new E");

        var report = _service.SaveWithinWorkspace(null, _root);

        report.Saved.ShouldBe([inside.FilePath!]);
        report.Refused.ShouldBe([outside.FilePath!]);
        File.ReadAllText(outside.FilePath!).ShouldBe("old");
        outside.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void NamedPaths_SaveOnlyThoseFiles()
    {
        var a = OpenDirty(_root, "A.cs", "old", "new A");
        var b = OpenDirty(_root, "B.cs", "old", "new B");

        var report = _service.SaveWithinWorkspace(["A.cs"], _root);

        report.Saved.ShouldBe([a.FilePath!]);
        File.ReadAllText(b.FilePath!).ShouldBe("old");
        b.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void NamedPath_ThatEscapesTheSolution_IsRefusedAndNothingIsWritten()
    {
        var outside = OpenDirty(_outside, "Elsewhere.cs", "old", "new E");

        var report = _service.SaveWithinWorkspace(["../" + Path.GetFileName(_outside) + "/Elsewhere.cs", outside.FilePath!], _root);

        report.Saved.ShouldBeEmpty();
        report.Refused.Count.ShouldBe(2);
        File.ReadAllText(outside.FilePath!).ShouldBe("old");
    }

    [Fact]
    public void NamedPath_NotOpenInTheEditor_IsReportedAndNeverCreated()
    {
        var missing = Path.Combine(_root, "Ghost.cs");

        var report = _service.SaveWithinWorkspace(["Ghost.cs"], _root);

        report.NotOpen.ShouldBe([missing]);
        File.Exists(missing).ShouldBeFalse();
    }

    [Fact]
    public void NamedPath_ThatIsOpenButClean_IsReportedAsAlreadySaved()
    {
        var path = Path.Combine(_root, "A.cs");
        File.WriteAllText(path, "text");
        _workspace.Open(path);

        var report = _service.SaveWithinWorkspace(["A.cs"], _root);

        report.AlreadySaved.ShouldBe([path]);
        report.Saved.ShouldBeEmpty();
    }

    [Fact]
    public void TheSameFileNamedTwice_IsSavedOnce()
    {
        var a = OpenDirty(_root, "A.cs", "old", "new A");

        var report = _service.SaveWithinWorkspace(["A.cs", a.FilePath!, "./A.cs"], _root);

        report.Saved.ShouldBe([a.FilePath!]);
    }

    [Fact]
    public void AFailedWrite_IsReported_AndLeavesTheDocumentDirty()
    {
        var a = OpenDirty(_root, "A.cs", "old", "new A");
        var b = OpenDirty(_root, "B.cs", "old", "new B");

        // An exclusive lock makes File.WriteAllText throw IOException, as an antivirus scan or another
        // process holding the file would.
        using var locked = new FileStream(a.FilePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var report = _service.SaveWithinWorkspace(null, _root);

        report.Failed.Select(f => f.Path).ShouldBe([a.FilePath!]);
        a.IsDirty.ShouldBeTrue();
        a.TextDocument.Text.ShouldBe("new A");
        // One locked file does not stop the rest.
        report.Saved.ShouldBe([b.FilePath!]);
        File.ReadAllText(b.FilePath!).ShouldBe("new B");
    }

    [Fact]
    public void UntitledDocuments_AreIgnored()
    {
        var untitled = _workspace.NewDocument();
        untitled.TextDocument.Text = "scratch";

        var report = _service.SaveWithinWorkspace(null, _root);

        report.Saved.ShouldBeEmpty();
        report.Failed.ShouldBeEmpty();
        untitled.IsDirty.ShouldBeTrue();
    }
}
