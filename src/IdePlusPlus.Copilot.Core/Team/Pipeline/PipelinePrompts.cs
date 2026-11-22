namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>Why a pipeline agent is asked to submit what it has now.</summary>
public enum WrapUpReason
{
    /// <summary>The stage's time share is used up.</summary>
    Time,

    /// <summary>The stage's (or the run's) token share is used up.</summary>
    Tokens,

    /// <summary>The conversation has grown too large to go on with.</summary>
    Context,

    /// <summary>The agent used every step it was allowed.</summary>
    Steps,
}

/// <summary>
/// The runner's own messages to a pipeline agent - the ones sent when an agent has not done what the
/// stage needs of it. Stage prompts (what each role is told to do) live with their stages; these are
/// the same for all of them.
///
/// Every agent's system prompt begins "You are &lt;name&gt;, " - the test client finds an agent by it, and
/// it is how an agent knows who it is - so any prompt added for a pipeline agent keeps that opening.
/// </summary>
public static class PipelinePrompts
{
    /// <summary>
    /// The agent answered without calling its submit tool. <paramref name="attempt"/> counts from 1.
    /// </summary>
    public static string MissingSubmission(string submitTool, int attempt, int attempts) =>
        $"You have not submitted your result. Nothing you wrote in the conversation is recorded - only a call to "
        + $"{submitTool} is. Call {submitTool} now with what you have (reminder {attempt} of {attempts}). "
        + "If you are unsure, submit your best answer and say what you could not establish in it.";

    /// <summary>
    /// Sent when the agent cannot be given more room: it must submit within a few more steps.
    /// </summary>
    public static string WrapUp(string submitTool, WrapUpReason reason) =>
        $"{Because(reason)} Stop exploring. Call {submitTool} now with what you have established so far, and say in "
        + "it what you did not get to. Do not start any new work.";

    private static string Because(WrapUpReason reason) => reason switch
    {
        WrapUpReason.Time => "Your stage has run out of time.",
        WrapUpReason.Tokens => "Your stage has used up its share of the token budget.",
        WrapUpReason.Context => "This conversation has grown too large to continue.",
        WrapUpReason.Steps => "You have used every step you were allowed.",
        _ => "You are out of room.",
    };
}
