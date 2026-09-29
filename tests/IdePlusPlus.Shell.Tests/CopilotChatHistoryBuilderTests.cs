using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Editor;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// Covers CopilotChatPaneViewModel.BuildChatHistory - the fix for a gap where a proposal decided
/// after its own turn had already stopped (the common manual-mode case, since AssistantTurnPolicy
/// stops the turn the moment a proposal needs a human) never made it back into the model's context
/// on a later turn: the user had to explain what happened by hand. SendAsync, ApprovePlanAsync, and
/// PersistChatHistory all now route through this one helper, so it's exercised directly here rather
/// than through any of them - internal, exposed to this project via InternalsVisibleTo.
/// </summary>
public sealed class CopilotChatHistoryBuilderTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "IdePlusPlusHistoryBuilderTests", Guid.NewGuid().ToString("N"));

    private readonly EditApplicationService _editService;

    public CopilotChatHistoryBuilderTests()
    {
        Directory.CreateDirectory(_root);
        _editService = new EditApplicationService(new EditorWorkspace(Path.Combine(_root, "recent-files.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private EditProposalViewModel CompletedEditProposal(string toolCallId = "call-1")
    {
        File.WriteAllText(Path.Combine(_root, "Foo.cs"), "a\nb\nc");
        var proposal = new EditProposalViewModel(toolCallId, _editService);
        proposal.Complete(
            new EditProposalEvent
            {
                ToolCallId = toolCallId,
                Status = EditProposalStatus.Complete,
                Operation = new EditOperation("Foo.cs", "replace_range", 2, 2, "B"),
                ArgumentsJson = """{"file":"Foo.cs"}""",
            },
            _root);
        return proposal;
    }

    [Fact]
    public void PlainChatMessages_AreCarriedThroughInOrder()
    {
        var messages = new ObservableObject[]
        {
            new ChatMessageViewModel(ChatRole.User) { Text = "hi" },
            new ChatMessageViewModel(ChatRole.Assistant) { Text = "hello" },
        };

        var history = CopilotChatPaneViewModel.BuildChatHistory(messages);

        history.Count.ShouldBe(2);
        history[0].Role.ShouldBe(ChatRole.User);
        history[0].Content.ShouldBe("hi");
        history[1].Role.ShouldBe(ChatRole.Assistant);
        history[1].Content.ShouldBe("hello");
    }

    [Fact]
    public void DecidedEditProposal_ReplaysAsAToolCallAndItsResult()
    {
        var proposal = CompletedEditProposal();
        proposal.AcceptCommand.Execute(null);

        var history = CopilotChatPaneViewModel.BuildChatHistory([proposal]);

        history.Count.ShouldBe(2);
        history[0].Role.ShouldBe(ChatRole.Assistant);
        history[0].ToolCalls.ShouldHaveSingleItem().Id.ShouldBe("call-1");
        history[0].ToolCalls![0].FunctionName.ShouldBe(ProposeEditTool.ToolName);
        history[1].Role.ShouldBe(ChatRole.Tool);
        history[1].ToolCallId.ShouldBe("call-1");
        history[1].Content.ShouldContain("Applied to Foo.cs");
    }

    /// <summary>The exact scenario reported: the model asked for an edit, it got held back for a
    /// decision, and the user accepted it after the turn had already stopped. This proves that
    /// acceptance is now visible on a later call to BuildChatHistory, not just in the UI.</summary>
    [Fact]
    public void EditAcceptedAfterItsTurnAlreadyStopped_StillShowsAsAppliedOnTheNextBuild()
    {
        var proposal = CompletedEditProposal();
        CopilotChatPaneViewModel.BuildChatHistory([proposal])[1].Content.ShouldContain("Waiting for the user");

        proposal.AcceptCommand.Execute(null);

        CopilotChatPaneViewModel.BuildChatHistory([proposal])[1].Content.ShouldContain("Applied to Foo.cs");
    }

    [Fact]
    public void UndecidedEditProposal_StillReplaysWithItsWaitingText()
    {
        // Every replayed tool call needs an answer or the next request is malformed - an ignored
        // proposal still has to produce one, not be silently dropped.
        var proposal = CompletedEditProposal();

        var history = CopilotChatPaneViewModel.BuildChatHistory([proposal]);

        history.Count.ShouldBe(2);
        history[1].Content.ShouldContain("Waiting for the user");
    }

    [Fact]
    public void StillStreamingEditProposal_IsSkipped()
    {
        var proposal = new EditProposalViewModel("call-1", _editService);

        CopilotChatPaneViewModel.BuildChatHistory([proposal]).ShouldBeEmpty();
    }

    [Fact]
    public void CompletedMcpToolCall_ReplaysWithItsFullUntruncatedResult()
    {
        var toolCall = new McpToolCallViewModel("call-2", "search_codebase", """{"query":"foo"}""");
        toolCall.Complete(isError: false, new string('x', 1000));

        var history = CopilotChatPaneViewModel.BuildChatHistory([toolCall]);

        history.Count.ShouldBe(2);
        history[0].ToolCalls.ShouldHaveSingleItem().FunctionName.ShouldBe("search_codebase");
        history[1].ToolCallId.ShouldBe("call-2");
        history[1].Content.Length.ShouldBe(1000);
    }

    [Fact]
    public void McpToolCallStillRunning_IsSkipped()
    {
        var toolCall = new McpToolCallViewModel("call-2", "search_codebase", null);

        CopilotChatPaneViewModel.BuildChatHistory([toolCall]).ShouldBeEmpty();
    }
}
