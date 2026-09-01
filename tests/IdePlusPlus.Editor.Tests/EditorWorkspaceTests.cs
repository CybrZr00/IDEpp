using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

public sealed class EditorWorkspaceTests : IDisposable
{
    private readonly string _recentFilesPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-recent-{Guid.NewGuid()}.json");
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        File.Delete(_recentFilesPath);
        foreach (var file in _tempFiles)
        {
            File.Delete(file);
        }
    }

    private string CreateTempFile(string content = "")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ideplusplus-ws-{Guid.NewGuid()}.txt");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    [Fact]
    public void NewDocument_AssignsIncrementingUntitledNames()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);

        var first = workspace.NewDocument();
        var second = workspace.NewDocument();

        first.Title.ShouldBe("Untitled1");
        second.Title.ShouldBe("Untitled2");
        workspace.OpenDocuments.Count.ShouldBe(2);
    }

    [Fact]
    public void Open_SameFileTwice_ReturnsTheSameDocumentInstance()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var path = CreateTempFile("content");

        var first = workspace.Open(path);
        var second = workspace.Open(path);

        second.ShouldBeSameAs(first);
        workspace.OpenDocuments.Count.ShouldBe(1);
    }

    [Fact]
    public void Open_MissingFile_ReturnsNullAndDoesNotThrow()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);

        var result = workspace.Open(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".txt"));

        result.ShouldBeNull();
    }

    [Fact]
    public void Open_MissingFile_PrunesStaleRecentFilesEntry()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var path = CreateTempFile("content");
        // Close it first - Open() only re-checks the filesystem when the path isn't already an
        // in-memory open document, which it otherwise still would be here.
        workspace.Close(workspace.Open(path)!);
        workspace.RecentFiles.ShouldContain(path);
        File.Delete(path);
        _tempFiles.Remove(path);

        workspace.Open(path);

        workspace.RecentFiles.ShouldNotContain(path);
    }

    [Fact]
    public void Open_AddsToFrontOfRecentFiles()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var pathA = CreateTempFile();
        var pathB = CreateTempFile();

        workspace.Open(pathA);
        workspace.Open(pathB);

        workspace.RecentFiles.First().ShouldBe(pathB);
    }

    [Fact]
    public void Open_RecentFilesPersistsAcrossInstances()
    {
        var path = CreateTempFile();
        new EditorWorkspace(_recentFilesPath).Open(path);

        var reloaded = new EditorWorkspace(_recentFilesPath);

        reloaded.RecentFiles.ShouldContain(path);
    }

    [Fact]
    public void Save_UntitledDocument_ThrowsInvalidOperationException()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var document = workspace.NewDocument();

        Should.Throw<InvalidOperationException>(() => workspace.Save(document));
    }

    [Fact]
    public void Save_WritesContentAndClearsDirtyFlag()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var path = CreateTempFile();
        var document = workspace.Open(path)!;
        document.TextDocument.Text = "updated content";

        workspace.Save(document);

        File.ReadAllText(path).ShouldBe("updated content");
        document.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void SaveAs_AttachesToNewPathAndWritesContent()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var document = workspace.NewDocument();
        document.TextDocument.Text = "brand new";
        var newPath = CreateTempFile();

        workspace.SaveAs(document, newPath);

        document.FilePath.ShouldBe(newPath);
        File.ReadAllText(newPath).ShouldBe("brand new");
    }

    [Fact]
    public void OpenOrCreate_ExistingFile_BehavesLikeOpen()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var path = CreateTempFile("content");

        var document = workspace.OpenOrCreate(path);

        document.ShouldNotBeNull();
        document.TextDocument.Text.ShouldBe("content");
        workspace.RecentFiles.ShouldContain(path);
    }

    [Fact]
    public void OpenOrCreate_MissingFile_OpensAnEmptyUnsavedDocumentBoundToThatPath()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".txt");

        var document = workspace.OpenOrCreate(path);

        document.ShouldNotBeNull();
        document.FilePath.ShouldBe(path);
        document.TextDocument.Text.ShouldBe("");
        document.IsDirty.ShouldBeFalse();
        workspace.OpenDocuments.ShouldContain(document);
    }

    [Fact]
    public void OpenOrCreate_SameMissingPathTwice_ReturnsTheSameDocumentInstance()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var path = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".txt");

        var first = workspace.OpenOrCreate(path);
        var second = workspace.OpenOrCreate(path);

        second.ShouldBeSameAs(first);
        workspace.OpenDocuments.Count.ShouldBe(1);
    }

    [Fact]
    public void Close_RemovesFromOpenDocuments()
    {
        var workspace = new EditorWorkspace(_recentFilesPath);
        var document = workspace.NewDocument();

        workspace.Close(document);

        workspace.OpenDocuments.ShouldNotContain(document);
    }
}
