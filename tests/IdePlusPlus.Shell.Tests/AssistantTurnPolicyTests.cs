using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// This exact decision has shipped three real bugs already: a produced proposal not being recorded
/// into history at all, Auto Mode auto-accepting an edit but the loop stopping right after anyway
/// (making the raised iteration cap for Auto Mode inert), and an edit the IDE refused stopping the
/// turn as though a person were being asked. Every combination of "what did this turn produce" is
/// covered here so a fourth regression fails a test instead of a live chat session.
/// </summary>
public class AssistantTurnPolicyTests
{
    private const EditOutcome Applied = EditOutcome.Applied;
    private const EditOutcome Awaiting = EditOutcome.AwaitingDecision;
    private const EditOutcome Rejected = EditOutcome.RejectedByUser;
    private const EditOutcome NotApplied = EditOutcome.NotApplied;

    [Fact]
    public void PlainTextReply_NothingPending_Stops()
    {
        Decide(toolCalls: 0, plan: false, autoMode: false).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlainTextReply_EvenWithAutoModeOn_Stops()
    {
        Decide(toolCalls: 0, plan: false, autoMode: true).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void ToolCallsOnly_NoProposals_ContinuesSilently()
    {
        Decide(toolCalls: 2, plan: false, autoMode: false).ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void ToolCallsOnly_AutoModeOnMakesNoDifference()
    {
        Decide(toolCalls: 1, plan: false, autoMode: true).ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void EditAwaitingTheUser_Stops_InEitherMode()
    {
        Decide(toolCalls: 0, plan: false, autoMode: false, Awaiting).ShouldBe(AssistantTurnAction.Stop);
        // Auto Mode holds a large deletion or a skill file back for confirmation: still a person's call.
        Decide(toolCalls: 0, plan: false, autoMode: true, Awaiting).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void EditAppliedInManualMode_Stops()
    {
        // The historical "propose_edit is terminal until the user decides" behavior, unchanged.
        Decide(toolCalls: 0, plan: false, autoMode: false, Applied).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void EditAppliedInAutoMode_ContinuesSilently()
    {
        // The bug: this used to Stop unconditionally, making Auto Mode's raised iteration cap inert.
        Decide(toolCalls: 0, plan: false, autoMode: true, Applied).ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void EditTheUserRejected_Stops_InEitherMode()
    {
        // Their decision, and the model has been told not to reapply it - it is theirs to speak next.
        Decide(toolCalls: 0, plan: false, autoMode: false, Rejected).ShouldBe(AssistantTurnAction.Stop);
        Decide(toolCalls: 0, plan: false, autoMode: true, Rejected).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void EditTheIdeRefused_HandsTheTurnBackToTheModel_InEitherMode()
    {
        // Outside the solution, a range that does not fit, a conflict, a hook veto: nobody is being
        // asked, and the tool result tells the model what to do differently. Stopping here left the
        // model's own correction for the user to request by hand.
        Decide(toolCalls: 0, plan: false, autoMode: false, NotApplied).ShouldBe(AssistantTurnAction.ContinueSilently);
        Decide(toolCalls: 0, plan: false, autoMode: true, NotApplied).ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void SeveralRefusedEdits_AllHandedBack()
    {
        Decide(toolCalls: 0, plan: false, autoMode: false, NotApplied, NotApplied, NotApplied)
            .ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void ARefusedEditAlongsideOneAwaitingTheUser_Stops()
    {
        // The model gets the refusal in the tool result either way, but there is still a card with
        // Accept and Reject on it, and carrying on past that would let a later turn pile onto it.
        Decide(toolCalls: 0, plan: false, autoMode: true, NotApplied, Awaiting).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void ARefusedEditAlongsideARejectedOne_Stops()
    {
        Decide(toolCalls: 0, plan: false, autoMode: true, NotApplied, Rejected).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void AutoMode_AppliedAndRefusedTogether_Continues()
    {
        Decide(toolCalls: 0, plan: false, autoMode: true, Applied, NotApplied)
            .ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void ManualMode_AppliedAndRefusedTogether_StillStops()
    {
        // Something was written and the user is stepping through edits; only an all-refused turn is
        // handed back.
        Decide(toolCalls: 0, plan: false, autoMode: false, Applied, NotApplied).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlanProposal_AlwaysStops_EvenWithAutoModeOn()
    {
        // Auto Mode never auto-approves a plan - only edits.
        Decide(toolCalls: 0, plan: true, autoMode: true).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlanProposal_AlongsideToolCalls_StillStops()
    {
        Decide(toolCalls: 2, plan: true, autoMode: true).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlanProposalTakesPrecedenceOverAnAppliedEditInTheSameTurn()
    {
        // Shouldn't be producible by the real model (propose_edit and propose_plan are never both
        // offered in the same turn - IsPlanMode picks exactly one), but the policy is defensively
        // plan-first regardless.
        Decide(toolCalls: 0, plan: true, autoMode: true, Applied).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void ToolCallsAndAnAppliedEditInTheSameTurn_AutoMode_ContinuesSilently()
    {
        // The model called a read-only tool AND proposed an edit in one turn - both should be
        // honored: the tool result is already in history by the time this is decided, and Auto Mode
        // resolving the edit should still let the model keep going.
        Decide(toolCalls: 1, plan: false, autoMode: true, Applied).ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void ToolCallsAndAnEditAwaitingTheUserInTheSameTurn_ManualMode_Stops()
    {
        Decide(toolCalls: 1, plan: false, autoMode: false, Awaiting).ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void ToolCallsAndARefusedEditInTheSameTurn_ContinuesSilently()
    {
        Decide(toolCalls: 1, plan: false, autoMode: false, NotApplied).ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    private static AssistantTurnAction Decide(int toolCalls, bool plan, bool autoMode, params EditOutcome[] edits) =>
        AssistantTurnPolicy.Decide(toolCalls, edits, plan, autoMode);
}
