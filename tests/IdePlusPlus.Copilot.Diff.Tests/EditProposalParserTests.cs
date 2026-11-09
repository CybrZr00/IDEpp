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

    [Fact]
    public void OnToolCallDelta_SpillsAnOversizedCallToATempFileInsteadOfKeepingItInMemory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ideplusplus-parser-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var parser = new EditProposalParser(maxArgumentChars: 1_000, spillDirectory: directory);
            parser.OnToolCallDelta(new ToolCallDelta(0, "call-big", "propose_edit", ""));

            var first = parser.OnToolCallDelta(new ToolCallDelta(0, null, null, "{\"file\": \"big.txt\", \"mode\": \"whole_file\", \"new_text\": \""));
            first.Status.ShouldBe(EditProposalStatus.Streaming);

            EditProposalEvent? last = null;
            for (var i = 0; i < 5; i++)
            {
                last = parser.OnToolCallDelta(new ToolCallDelta(0, null, null, new string('x', 400)));
            }

            last!.Status.ShouldBe(EditProposalStatus.Oversized);
            last.SpillPath.ShouldNotBeNull();
            last.TotalChars.ShouldBeGreaterThan(1_000);
            File.ReadAllText(last.SpillPath!).Length.ShouldBe((int)last.TotalChars);

            // Later fragments, including the one that would have closed the object, keep going to the
            // file and never produce an operation.
            var closing = parser.OnToolCallDelta(new ToolCallDelta(0, null, null, "\"}"));
            closing.Status.ShouldBe(EditProposalStatus.Oversized);
            closing.Operation.ShouldBeNull();
            File.ReadAllText(closing.SpillPath!).ShouldEndWith("\"}");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void OnToolCallDelta_StillCompletesALargeButUnderLimitCall()
    {
        var parser = new EditProposalParser();
        var body = new string('y', 200_000);

        parser.OnToolCallDelta(new ToolCallDelta(0, "call-large", "propose_edit", "{\"file\": \"a.txt\", \"mode\": \"whole_file\", \"new_text\": \""));
        for (var i = 0; i < 20; i++)
        {
            parser.OnToolCallDelta(new ToolCallDelta(0, null, null, body[..10_000]));
        }

        var done = parser.OnToolCallDelta(new ToolCallDelta(0, null, null, "\"}"));

        done.Status.ShouldBe(EditProposalStatus.Complete);
        done.Operation!.NewText.Length.ShouldBe(200_000);
    }
}
