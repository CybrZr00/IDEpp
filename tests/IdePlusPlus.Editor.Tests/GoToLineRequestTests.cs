using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Editor.Tests;

public sealed class GoToLineRequestTests : IDisposable
{
    private readonly string _recentFilesPath = Path.Combine(Path.GetTempPath(), $"ideplusplus-recent-{Guid.NewGuid()}.json");
    private readonly EditorWorkspace _workspace;

    public GoToLineRequestTests()
    {
        _workspace = new EditorWorkspace(_recentFilesPath);
    }

    public void Dispose() => File.Delete(_recentFilesPath);

    [Fact]
    public void Consume_ReturnsAndClearsThePendingEntry()
    {
        var request = new GoToLineRequest();
        var document = _workspace.NewDocument();

        request.Request(document, 42, 7);

        request.Consume(document).ShouldBe((42, 7));
        request.Consume(document).ShouldBeNull();
    }

    [Fact]
    public void Consume_ReturnsNullForADocumentWithNoPendingRequest()
    {
        var request = new GoToLineRequest();

        request.Consume(_workspace.NewDocument()).ShouldBeNull();
    }

    [Fact]
    public void Request_WithNoColumn_DefaultsToNull()
    {
        var request = new GoToLineRequest();
        var document = _workspace.NewDocument();

        request.Request(document, 5);

        request.Consume(document).ShouldBe((5, null));
    }

    [Fact]
    public void ASecondRequestBeforeConsumingOverwritesTheEarlierOne()
    {
        var request = new GoToLineRequest();
        var document = _workspace.NewDocument();

        request.Request(document, 1, null);
        request.Request(document, 2, 5);

        request.Consume(document).ShouldBe((2, 5));
    }

    [Fact]
    public void Request_FiresTheEventSynchronouslyWithTheSameLineAndColumn()
    {
        var request = new GoToLineRequest();
        var document = _workspace.NewDocument();
        (DocumentViewModel Document, int Line, int? Column)? received = null;
        request.Requested += (doc, line, column) => received = (doc, line, column);

        request.Request(document, 10, 3);

        received.ShouldBe((document, 10, 3));
    }

    [Fact]
    public void PendingEntriesAreIndependentPerDocument()
    {
        var request = new GoToLineRequest();
        var first = _workspace.NewDocument();
        var second = _workspace.NewDocument();

        request.Request(first, 1, null);

        request.Consume(second).ShouldBeNull();
        request.Consume(first).ShouldBe((1, null));
    }
}
