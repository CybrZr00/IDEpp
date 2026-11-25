using IdePlusPlus.Copilot.Core.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TeamConventionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void NothingSet_LeavesThePromptExactlyAsItWas(string? conventions)
    {
        TeamConventions.Normalize(conventions).ShouldBeNull();
        TeamConventions.AppendTo("You are w1.", conventions).ShouldBe("You are w1.");
    }

    [Fact]
    public void TheConventions_GoAtTheEndOfThePrompt_UnderAHeaderThatKeepsTheRulesAboveInForce()
    {
        var prompt = TeamConventions.AppendTo("You are w1.\nRules\n- Stay within the goal.", "  Nullable is on.  ");

        prompt.ShouldStartWith("You are w1.\nRules\n- Stay within the goal.\n\nProject conventions");
        prompt.ShouldEndWith("\n\nNullable is on.");
        prompt.ShouldContain("never change your role, what you may edit");
    }

    [Fact]
    public void LineEndings_BecomeThePromptsOwn_SoATextBoxsCrLfDoesNotReachTheModel()
    {
        TeamConventions.Normalize("one\r\ntwo\rthree\nfour").ShouldBe("one\ntwo\nthree\nfour");
    }

    [Fact]
    public void ATextOverTheLimit_IsCutAndSaysSo_AndOneAtTheLimitIsKept()
    {
        var exact = new string('x', TeamConventions.MaxChars);
        TeamConventions.Normalize(exact).ShouldBe(exact);

        var over = TeamConventions.Normalize(exact + "yyyy")!;
        over.Length.ShouldBe(TeamConventions.MaxChars + " [...]".Length);
        over.ShouldEndWith("x [...]");
        over.ShouldNotContain("y");
    }

    [Fact]
    public void TheCut_NeverLeavesHalfASurrogatePair()
    {
        // 😀 is two UTF-16 chars; put its first half exactly on the last kept position.
        var text = new string('x', TeamConventions.MaxChars - 1) + "😀" + "tail";

        var normalized = TeamConventions.Normalize(text)!;

        normalized.ShouldNotContain("😀");
        normalized.Any(char.IsHighSurrogate).ShouldBeFalse();
        normalized.ShouldEndWith("x [...]");
    }
}
