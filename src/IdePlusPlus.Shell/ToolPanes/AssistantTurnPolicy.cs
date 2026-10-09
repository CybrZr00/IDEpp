namespace IdePlusPlus.Shell.ToolPanes;

public enum AssistantTurnAction
{
    /// <summary>End the conversational turn - either nothing is pending (a plain text reply), or
    /// something produced in this turn needs an explicit human decision before the model continues.</summary>
    Stop,

    /// <summary>Loop again without telling the model anything extra - it already has everything it
    /// needs in the tool results just appended to history, edit outcomes included.</summary>
    ContinueSilently,
}

/// <summary>Where a propose_edit call ended up by the time its turn was decided on.</summary>
public enum EditOutcome
{
    /// <summary>Written. Auto Mode accepted it, or the user did.</summary>
    Applied,

    /// <summary>Waiting for the user to accept or reject: shown for review, or held back from Auto Mode
    /// (a large deletion, a skill file). The one outcome that genuinely needs a person.</summary>
    AwaitingDecision,

    /// <summary>The user said no. A decision the model should not talk over.</summary>
    RejectedByUser,

    /// <summary>Not written, and nobody is being asked: the IDE refused it (outside the solution, a line
    /// range that does not fit, an elided placeholder), the file changed underneath it, or a hook
    /// blocked it. The model has been told exactly why in the tool result - which is only worth
    /// anything if it gets another go.</summary>
    NotApplied,
}

/// <summary>
/// Decides what RunAssistantTurnsAsync should do after one streamed turn, pulled out as a pure
/// function because this exact decision has caused user-visible bugs in three sessions now: first
/// a produced proposal wasn't recorded in history at all (the model couldn't tell it had already
/// proposed), then Auto Mode auto-accepted an edit but the loop still stopped right after (nothing
/// ever made it continue past a resolved proposal, so raising the iteration cap for Auto Mode was
/// inert - the loop could never reach it for a single-edit turn), and then an edit the IDE had
/// refused stopped the turn as if it were waiting on a person, when nobody was and the model had
/// just been told how to fix it. A pure function is something a unit test can pin down completely;
/// a 40-line method threaded through dispatcher.Invoke and live HTTP streaming is not.
/// </summary>
public static class AssistantTurnPolicy
{
    public static AssistantTurnAction Decide(
        int toolCallCount, IReadOnlyList<EditOutcome> editOutcomes, bool producedPlanProposal, bool isAutoMode)
    {
        // A plan always needs an explicit human Approve click, no matter what - Auto Mode only ever
        // auto-resolves edits, never plans.
        if (producedPlanProposal)
        {
            return AssistantTurnAction.Stop;
        }

        if (editOutcomes.Count > 0)
        {
            // A person is either being asked or has just answered: either way it is theirs to carry on.
            if (editOutcomes.Any(o => o is EditOutcome.AwaitingDecision or EditOutcome.RejectedByUser))
            {
                return AssistantTurnAction.Stop;
            }

            // Manual mode steps through edits one at a time, so an applied edit still ends the turn. An
            // edit nobody is waiting on does not: nothing was applied and nothing is pending, so
            // stopping would just leave the model's corrected retry for the user to ask for.
            if (!isAutoMode && editOutcomes.Any(o => o == EditOutcome.Applied))
            {
                return AssistantTurnAction.Stop;
            }

            return AssistantTurnAction.ContinueSilently;
        }

        if (toolCallCount == 0)
        {
            return AssistantTurnAction.Stop; // plain text reply, nothing pending
        }

        // Auto-accepted edits used to need a synthetic note here, because nothing recorded that the
        // edit had happened. propose_edit is now replayed into history as a real tool call with a
        // real tool result, so there is nothing left to tell the model separately.
        return AssistantTurnAction.ContinueSilently;
    }
}
