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

    /// <summary>What a submit tool says when it refuses a submission: the reasons, and to fix them and
    /// call again. Specific on purpose - a model told only that it failed sends the same thing twice.</summary>
    public static string NotRecorded(string submitTool, string feedback) =>
        $"Not recorded:\n{feedback}\nFix these and call {submitTool} again.";

    /// <summary>
    /// A surveyor's system prompt: who it is, what it is for (the role's charter), what it can and cannot
    /// do, and that only its submission counts. It is told the user is not available, since the one thing
    /// a read-only agent could do with a doubt is ask.
    /// </summary>
    public static string Surveyor(string name, TeamRole role) =>
        $"You are {name}, a surveyor in an Ultra Pipeline run in IDE++. {role.Charter}\n\n"
        + "You work alone and read-only, on a copy of the code as it stands. You cannot change anything, and "
        + "nobody can answer a question - the user is not available - so settle what you can by reading. Use "
        + "read_file, list_files, search_codebase, find_symbol and find_references. When you have read enough, call "
        + $"{PipelineStageTools.SurveySubmitToolName} once; nothing you write in the conversation is recorded, only "
        + "that submission. The designer reads every survey and has little room for them: a few items with evidence "
        + "beat a long list, and every item names the file that shows it.";

    public static string SurveyorOpening(string goal, SurveyFocus focus)
    {
        var question = string.IsNullOrWhiteSpace(focus.Question) ? "" : $"\nThe question that matters most there: {focus.Question}";
        return $"The goal of the whole run:\n{goal.Trim()}\n\nYour area: {focus.Area}{question}\n\n"
               + $"Survey your area, then call {PipelineStageTools.SurveySubmitToolName}.";
    }

    private static string Because(WrapUpReason reason) => reason switch
    {
        WrapUpReason.Time => "Your stage has run out of time.",
        WrapUpReason.Tokens => "Your stage has used up its share of the token budget.",
        WrapUpReason.Context => "This conversation has grown too large to continue.",
        WrapUpReason.Steps => "You have used every step you were allowed.",
        _ => "You are out of room.",
    };
}
