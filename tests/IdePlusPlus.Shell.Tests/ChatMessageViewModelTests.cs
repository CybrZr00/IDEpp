using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public class ChatMessageViewModelTests
{
    [Fact]
    public void NewMessage_HasNoReasoningByDefault()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);

        message.HasReasoning.ShouldBeFalse();
        message.ReasoningText.ShouldBe("");
    }

    [Fact]
    public void AppendReasoning_AccumulatesAndSetsHasReasoning()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);

        message.AppendReasoning("Let me ");
        message.AppendReasoning("think about this.");

        message.HasReasoning.ShouldBeTrue();
        message.ReasoningText.ShouldBe("Let me think about this.");
    }

    [Fact]
    public void ReasoningAndTextAreIndependent()
    {
        var message = new ChatMessageViewModel(ChatRole.Assistant);

        message.AppendReasoning("thinking");
        message.Append("the answer");

        message.ReasoningText.ShouldBe("thinking");
        message.Text.ShouldBe("the answer");
    }
}
