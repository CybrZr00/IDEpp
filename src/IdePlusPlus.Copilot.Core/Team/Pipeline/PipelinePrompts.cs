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

    /// <summary>
    /// The designer's system prompt: who it is, the role's charter, and how the stage works - what it may
    /// do in its own checkout, what the contract commit is, and that only <c>design_submit</c> counts. The
    /// limits are the run's, stated so the first design is not refused for them.
    /// </summary>
    public static string Designer(string name, TeamRole role, DesignLimits limits) =>
        $"You are {name}, the designer in an Ultra Pipeline run in IDE++. {role.Charter}\n\n"
        + "You have your own git checkout of the code as it stands, on your own branch: you can read it, edit it, "
        + "build it and commit. The user is not available - never ask them anything. Work in this order.\n"
        + "1. Read the surveys you are given and as much of the code as you need.\n"
        + "2. Decide the files the work touches and give each exactly one owner, named b1, b2, ... in order - at most "
        + $"{limits.MaxOwners} owners and {limits.MaxFiles} files. List EVERY file an owner will create or change - "
        + "source, tests, docs - because an owner may edit only the files it owns. A file that does not exist yet is "
        + "listed too; its owner creates it. Paths are relative to the solution folder, with forward slashes.\n"
        + "3. Write the interface contract in your checkout: only types, signatures and members, with bodies that throw "
        + "NotImplementedException, so every owner builds against the same contract and nothing is implemented yet. "
        + "Change no other file. Run build_solution until it passes, then team_commit.\n"
        + $"4. Call {PipelineStageTools.DesignSubmitToolName} once, marking the files you wrote with isStub: your commit "
        + "may change those files and no others. If the work needs no new interface, write no stubs and commit nothing.\n"
        + $"Nothing you write in the conversation is recorded - only {PipelineStageTools.DesignSubmitToolName} is. The "
        + "runner checks the design, checks that your commit changes only the declared stubs, builds it, and lands it "
        + "for the owners; a refusal says exactly what to fix, and you call it again. Give every file at least one "
        + $"acceptance criterion, and list up to {limits.MaxMisuseCases} misuse cases (M1, M2, ...).";

    /// <summary>
    /// The designer's first message: the goal, the surveys (fenced - they are other agents' words about a
    /// repository, which is where an attacker's sentence would sit) and which areas have none.
    /// </summary>
    /// <param name="surveys">The surveys that were handed in, as the designer reads them.</param>
    /// <param name="missingSurveys">Who gave none and what they were to cover, so the designer covers it itself.</param>
    public static string DesignerOpening(
        string goal, string? approvedPlan, IReadOnlyList<SurveyReport> surveys, IReadOnlyList<string> missingSurveys)
    {
        var text = new System.Text.StringBuilder($"The goal of the whole run:\n{goal.Trim()}\n\n");
        if (!string.IsNullOrWhiteSpace(approvedPlan))
        {
            text.Append($"Approved plan (the user approved these steps):\n{approvedPlan.Trim()}\n\n");
        }

        text.AppendLine(DataFence.Instruction);
        text.AppendLine();
        if (surveys.Count == 0)
        {
            text.AppendLine("No surveys were made for this run: read the code you need yourself.");
        }

        foreach (var survey in surveys)
        {
            text.AppendLine(DataFence.Wrap($"survey of {survey.Area}", survey.Render()));
            text.AppendLine();
        }

        if (missingSurveys.Count > 0)
        {
            text.AppendLine($"These surveys were not handed in, so cover those areas yourself: {string.Join("; ", missingSurveys)}.");
            text.AppendLine();
        }

        text.Append($"Design the work, write and commit the contract, then call {PipelineStageTools.DesignSubmitToolName}.");
        return text.ToString();
    }

    /// <summary>
    /// An owner's system prompt: who it is, the role's charter, the files it owns, and how the stage works - build them,
    /// merge small and early, report once. What it may not do is enforced by its tools, so the prompt says it plainly
    /// rather than leaving the model to find out from a refusal.
    /// </summary>
    public static string Owner(string name, TeamRole role, IReadOnlyList<string> files) =>
        $"You are {name}, an owner in an Ultra Pipeline run in IDE++. {role.Charter}\n\n"
        + "You have your own git checkout of the code, on your own branch. It starts from the design's contract: the "
        + $"interface stubs are already in it. You own exactly these files: {string.Join(", ", files)}. Nobody else may "
        + "edit them, and you may edit no others. Work in this order.\n"
        + "1. Read the design, then the contract and the code around your files.\n"
        + "2. Build your files to their acceptance criteria. Run build_solution until it passes, and run_tests for the "
        + "tests that cover your files. Then team_commit.\n"
        + "3. Merge small and early: team_merge puts your commit on the shared team branch once it builds, and your "
        + "peers build against that branch. If the team branch has moved on, team_sync, build again, then team_merge.\n"
        + $"4. When every file you own meets its acceptance criteria and everything is merged, call "
        + $"{PipelineStageTools.BuildSubmitToolName} once. Work that is not merged is lost when the run ends.\n"
        + $"The user is not available - never ask them anything. Nothing you write in the conversation is recorded - only "
        + $"{PipelineStageTools.BuildSubmitToolName} is.";

    /// <summary>
    /// An owner's first message: the goal and the whole design (fenced - the designer's words, from which an owner
    /// learns its files and what the others build), then which files are its own.
    /// </summary>
    public static string OwnerOpening(string goal, string? approvedPlan, DesignPlan design, string name, IReadOnlyList<string> files)
    {
        var text = new System.Text.StringBuilder($"The goal of the whole run:\n{goal.Trim()}\n\n");
        if (!string.IsNullOrWhiteSpace(approvedPlan))
        {
            text.Append($"Approved plan (the user approved these steps):\n{approvedPlan.Trim()}\n\n");
        }

        text.AppendLine(DataFence.Instruction);
        text.AppendLine();
        text.AppendLine(DataFence.Wrap("design", design.Render()));
        text.AppendLine();
        text.AppendLine($"You are {name} and you own exactly these files: {string.Join(", ", files)}.");
        text.Append($"Build your files, merge them, then call {PipelineStageTools.BuildSubmitToolName}.");
        return text.ToString();
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
