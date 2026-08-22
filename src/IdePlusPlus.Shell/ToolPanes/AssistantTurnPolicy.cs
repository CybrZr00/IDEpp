namespace IdePlusPlus.Shell.ToolPanes;

public enum AssistantTurnAction
{
    /// <summary>End the conversational turn - either nothing is pending (a plain text reply), or
    /// something produced in this turn needs an explicit human decision before the model continues.</summary>
    Stop,

    /// <summary>Loop again without telling the model anything extra - it already has everything it
    /// needs in the tool results just appended to history.</summary>
    ContinueSilently,

    /// <summary>Loop again, but first tell the model what just happened - nothing else records that
    /// an edit proposal was auto-accepted (see CopilotChatPaneViewModel's HandleToolCallDelta), so
    /// without this note the model has no way to know its edit actually landed.</summary>
    ContinueWithAppliedEditsNote,
}

/// <summary>
/// Decides what RunAssistantTurnsAsync should do after one streamed turn, pulled out as a pure
/// function because this exact decision has caused two real, user-visible bugs in one session: first
/// a produced proposal wasn't recorded in history at all (the model couldn't tell it had already
/// proposed), then Auto Mode auto-accepted an edit but the loop still stopped right after (nothing
/// ever made it continue past a resolved proposal, so raising the iteration cap for Auto Mode was
/// inert - the loop could never reach it for a single-edit turn). A pure function is something a
/// unit test can pin down completely; a 40-line method threaded through dispatcher.Invoke and live
/// HTTP streaming is not.
/// </summary>
public static class AssistantTurnPolicy
{
    public static AssistantTurnAction Decide(int toolCallCount, int editProposalCount, bool allEditProposalsApplied, bool producedPlanProposal, bool isAutoMode)
    {
        // A plan always needs an explicit human Approve click, no matter what - Auto Mode only ever
        // auto-resolves edits, never plans.
        if (producedPlanProposal)
        {
            return AssistantTurnAction.Stop;
        }

        var autoAcceptedAllEdits = editProposalCount > 0 && isAutoMode && allEditProposalsApplied;
        if (editProposalCount > 0 && !autoAcceptedAllEdits)
        {
            // At least one edit still needs a human decision (Auto Mode is off, or a proposal was
            // rejected, or conflicted) - manual mode's proposal-is-terminal behavior, unchanged.
            return AssistantTurnAction.Stop;
        }

        if (toolCallCount == 0 && editProposalCount == 0)
        {
            return AssistantTurnAction.Stop; // plain text reply, nothing pending
        }

        return autoAcceptedAllEdits ? AssistantTurnAction.ContinueWithAppliedEditsNote : AssistantTurnAction.ContinueSilently;
    }
}
