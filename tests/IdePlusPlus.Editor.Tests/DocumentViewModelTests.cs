using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

public sealed class DocumentViewModelTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"ideplusplus-doc-{Guid.NewGuid()}.txt");

    public void Dispose()
    {
        if (File.Exists(_tempFile))
        {
            File.Delete(_tempFile);
        }
    }

    [Fact]
    public void ForNewFile_StartsCleanWithUntitledTitle()
    {
        var document = DocumentViewModel.ForNewFile("Untitled1");

        document.FilePath.ShouldBeNull();
        document.IsDirty.ShouldBeFalse();
        document.Title.ShouldBe("Untitled1");
        document.ContentId.ShouldBe("untitled://Untitled1");
    }

    [Fact]
    public void ForExistingFile_LoadsContentWithoutMarkingDirty()
    {
        File.WriteAllText(_tempFile, "hello world");

        var document = DocumentViewModel.ForExistingFile(_tempFile);

        document.TextDocument.Text.ShouldBe("hello world");
        document.IsDirty.ShouldBeFalse();
        document.FilePath.ShouldBe(_tempFile);
        document.ContentId.ShouldBe(_tempFile);
    }

    [Fact]
    public void EditingTextDocument_MarksDirtyAndAppendsAsteriskToTitle()
    {
        var document = DocumentViewModel.ForNewFile("Untitled1");

        document.TextDocument.Text = "new content";

        document.IsDirty.ShouldBeTrue();
        document.Title.ShouldBe("Untitled1*");
    }

    [Fact]
    public void AttachToFile_UpdatesFilePathContentIdAndTitle()
    {
        var document = DocumentViewModel.ForNewFile("Untitled1");
        var newPath = Path.Combine(Path.GetTempPath(), "renamed.cs");

        document.AttachToFile(newPath);

        document.FilePath.ShouldBe(newPath);
        document.ContentId.ShouldBe(newPath);
        document.Title.ShouldBe("renamed.cs");
    }
}
