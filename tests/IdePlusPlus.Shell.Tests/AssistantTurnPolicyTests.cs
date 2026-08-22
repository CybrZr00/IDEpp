using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// This exact decision has shipped two real bugs already: a produced proposal not being recorded
/// into history at all, and Auto Mode auto-accepting an edit but the loop stopping right after
/// anyway (making the raised iteration cap for Auto Mode inert). Every combination of "what did this
/// turn produce" is covered here so a third regression fails a test instead of a live chat session.
/// </summary>
public class AssistantTurnPolicyTests
{
    [Fact]
    public void PlainTextReply_NothingPending_Stops()
    {
        Decide(toolCalls: 0, editProposals: 0, allApplied: false, plan: false, autoMode: false)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlainTextReply_EvenWithAutoModeOn_Stops()
    {
        Decide(toolCalls: 0, editProposals: 0, allApplied: false, plan: false, autoMode: true)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void ToolCallsOnly_NoProposals_ContinuesSilently()
    {
        Decide(toolCalls: 2, editProposals: 0, allApplied: false, plan: false, autoMode: false)
            .ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void ToolCallsOnly_AutoModeOnMakesNoDifference()
    {
        Decide(toolCalls: 1, editProposals: 0, allApplied: false, plan: false, autoMode: true)
            .ShouldBe(AssistantTurnAction.ContinueSilently);
    }

    [Fact]
    public void EditProposal_ManualMode_Stops()
    {
        // The historical "propose_edit is terminal until the user decides" behavior, unchanged.
        Decide(toolCalls: 0, editProposals: 1, allApplied: true, plan: false, autoMode: false)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void EditProposal_AutoModeOnAndApplied_ContinuesWithNote()
    {
        // The bug: this used to Stop unconditionally, making Auto Mode's raised iteration cap inert.
        Decide(toolCalls: 0, editProposals: 1, allApplied: true, plan: false, autoMode: true)
            .ShouldBe(AssistantTurnAction.ContinueWithAppliedEditsNote);
    }

    [Fact]
    public void EditProposal_AutoModeOnButNotApplied_Stops()
    {
        // A rejection or a conflict (file changed since proposed) still needs a human, even in Auto Mode.
        Decide(toolCalls: 0, editProposals: 1, allApplied: false, plan: false, autoMode: true)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void MultipleEditProposals_AutoModeOn_AllMustHaveApplied()
    {
        // allEditProposalsApplied is the caller's All(...) result - false here models "at least one
        // of several proposals in this turn didn't apply".
        Decide(toolCalls: 0, editProposals: 3, allApplied: false, plan: false, autoMode: true)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlanProposal_AlwaysStops_EvenWithAutoModeOn()
    {
        // Auto Mode never auto-approves a plan - only edits.
        Decide(toolCalls: 0, editProposals: 0, allApplied: false, plan: true, autoMode: true)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlanProposal_AlongsideToolCalls_StillStops()
    {
        Decide(toolCalls: 2, editProposals: 0, allApplied: false, plan: true, autoMode: true)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void PlanProposalTakesPrecedenceOverAnAppliedEditInTheSameTurn()
    {
        // Shouldn't be producible by the real model (propose_edit and propose_plan are never both
        // offered in the same turn - IsPlanMode picks exactly one), but the policy is defensively
        // plan-first regardless.
        Decide(toolCalls: 0, editProposals: 1, allApplied: true, plan: true, autoMode: true)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    [Fact]
    public void ToolCallsAndAnAppliedEditInTheSameTurn_AutoMode_ContinuesWithNote()
    {
        // The model called a read-only tool AND proposed an edit in one turn - both should be
        // honored: the tool result is already in history by the time this is decided, and Auto Mode
        // resolving the edit should still let the model keep going.
        Decide(toolCalls: 1, editProposals: 1, allApplied: true, plan: false, autoMode: true)
            .ShouldBe(AssistantTurnAction.ContinueWithAppliedEditsNote);
    }

    [Fact]
    public void ToolCallsAndAnUnresolvedEditInTheSameTurn_ManualMode_Stops()
    {
        Decide(toolCalls: 1, editProposals: 1, allApplied: true, plan: false, autoMode: false)
            .ShouldBe(AssistantTurnAction.Stop);
    }

    private static AssistantTurnAction Decide(int toolCalls, int editProposals, bool allApplied, bool plan, bool autoMode) =>
        AssistantTurnPolicy.Decide(toolCalls, editProposals, allApplied, plan, autoMode);
}
