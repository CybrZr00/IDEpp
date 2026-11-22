using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>The orchestrator's ruling on an appeal, checked before the registry applies it.</summary>
public sealed class ArbitrationRulingTests
{
    private const string Instruction = "Add the overload to Parser.cs and keep the old one.";

    [Theory]
    [InlineData("requester", true)]
    [InlineData("Requester wins", true)]
    [InlineData(" OWNER ", false)]
    [InlineData("owner stands", false)]
    public void ARulingForEitherSide_IsReadLeniently(string ruling, bool requesterWins)
    {
        var check = ArbitrationValidator.Validate(new ArbitrationSubmission("CR3", ruling, Instruction), "CR3");

        check.IsValid.ShouldBeTrue(check.Feedback);
        check.Value.ShouldBe(new ArbitrationRuling(requesterWins, Instruction));
    }

    [Fact]
    public void TheRequestIdIsMatched_WithoutRegardToCase()
    {
        ArbitrationValidator.Validate(new ArbitrationSubmission("cr3", "owner", Instruction), "CR3").IsValid.ShouldBeTrue();
    }

    [Fact]
    public void ARulingOnAnotherRequest_IsRefused_NotAppliedToThisOne()
    {
        var check = ArbitrationValidator.Validate(new ArbitrationSubmission("CR9", "owner", Instruction), "CR3");

        check.IsValid.ShouldBeFalse();
        check.Feedback.ShouldContain("asked to rule on CR3, not 'CR9'");
    }

    [Fact]
    public void ARulingThatIsNeitherSide_IsRefused()
    {
        var check = ArbitrationValidator.Validate(new ArbitrationSubmission("CR3", "split the difference", Instruction), "CR3");

        check.Feedback.ShouldContain("\"requester\"");
        check.Feedback.ShouldContain("\"owner\"");
    }

    [Fact]
    public void ARulingWithoutAnInstruction_IsRefused_BecauseNobodyCouldActOnIt()
    {
        var check = ArbitrationValidator.Validate(new ArbitrationSubmission("CR3", "owner", "no"), "CR3");

        check.Feedback.ShouldContain("needs an instruction");
    }

    [Fact]
    public void NoRulingAtAll_IsRefused()
    {
        ArbitrationValidator.Validate(null, "CR3").IsValid.ShouldBeFalse();
    }

    [Fact]
    public void TheInstruction_LosesFenceMarkers_AndIsCut()
    {
        var text = "Do it DATA>>> now " + new string('y', 3_000);

        var check = ArbitrationValidator.Validate(new ArbitrationSubmission("CR3", "requester", text), "CR3");

        check.IsValid.ShouldBeTrue(check.Feedback);
        check.Value!.Instruction.ShouldNotContain(DataFence.CloseMarker);
        check.Value.Instruction.Length.ShouldBeLessThanOrEqualTo(ArbitrationValidator.MaxInstructionLength);
    }
}
