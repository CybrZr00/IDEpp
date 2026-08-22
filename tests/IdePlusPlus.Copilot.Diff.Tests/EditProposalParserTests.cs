using IdePlusPlus.Copilot.Core;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Diff.Tests;

public class EditProposalParserTests
{
    // The exact fragment sequence captured from a real OpenRouter/Claude Sonnet 4.5 streamed
    // tool call during the M5 spike - not synthesized, so this locks in the real wire format:
    // arguments arrive as incremental fragments that must be concatenated, and id/name are only
    // present on the first delta for a given tool-call index.
    private static readonly (string? Id, string? Name, string? ArgumentsFragment)[] RealFragmentSequence =
    [
        ("toolu_bdrk_01MJa9cjS6GzPEBf1bcrb5Kz", "propose_edit", ""),
        (null, null, ""),
        (null, null, "{\"file\": \"hello.txt"),
        (null, null, "\", \"mode\": \"whole_file"),
        (null, null, "\", \"new_text\": \"Hello from the"),
        (null, null, " copilot!"),
        (null, null, "\"}"),
    ];

    [Fact]
    public void OnToolCallDelta_ReconstructsOperationFromRealFragmentSequence()
    {
        var parser = new EditProposalParser();
        EditProposalEvent? lastEvent = null;

        foreach (var (id, name, fragment) in RealFragmentSequence)
        {
            lastEvent = parser.OnToolCallDelta(new ToolCallDelta(0, id, name, fragment));
        }

        lastEvent!.Status.ShouldBe(EditProposalStatus.Complete);
        lastEvent.Operation!.FilePath.ShouldBe("hello.txt");
        lastEvent.Operation.Mode.ShouldBe("whole_file");
        lastEvent.Operation.NewText.ShouldBe("Hello from the copilot!");
        lastEvent.Operation.IsWholeFile.ShouldBeTrue();
    }

    [Fact]
    public void OnToolCallDelta_GivesLivePreviewOfFileAndNewTextWhileStillStreaming()
    {
        var parser = new EditProposalParser();

        // Everything up to (not including) the final closing fragment - still invalid JSON.
        EditProposalEvent? midStreamEvent = null;
        foreach (var (id, name, fragment) in RealFragmentSequence[..^1])
        {
            midStreamEvent = parser.OnToolCallDelta(new ToolCallDelta(0, id, name, fragment));
        }

        midStreamEvent!.Status.ShouldBe(EditProposalStatus.Streaming);
        midStreamEvent.FilePathPreview.ShouldBe("hello.txt");
        midStreamEvent.NewTextPreview.ShouldBe("Hello from the copilot!");
    }

    [Fact]
    public void OnToolCallDelta_TracksMultipleConcurrentToolCallsByIndexIndependently()
    {
        var parser = new EditProposalParser();

        parser.OnToolCallDelta(new ToolCallDelta(0, "call-a", "propose_edit", ""));
        parser.OnToolCallDelta(new ToolCallDelta(1, "call-b", "propose_edit", ""));
        parser.OnToolCallDelta(new ToolCallDelta(0, null, null, "{\"file\": \"a.txt\", \"mode\": \"whole_file\", \"new_text\": \"A\"}"));
        var second = parser.OnToolCallDelta(new ToolCallDelta(1, null, null, "{\"file\": \"b.txt\", \"mode\": \"whole_file\", \"new_text\": \"B\"}"));

        second.ToolCallId.ShouldBe("call-b");
        second.Operation!.FilePath.ShouldBe("b.txt");
    }
}
