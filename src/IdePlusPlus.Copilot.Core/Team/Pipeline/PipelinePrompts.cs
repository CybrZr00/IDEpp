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
        + "4. A file you do not own is not yours to edit. If your work needs one changed, request_change asks its owner - say "
        + "what and why; it returns at once with an id - and await_change waits for the answer when you need it. If it is "
        + "accepted and done, team_sync brings the change in. A rejection with reasons can be appealed once, to the lead, "
        + "whose decision is final. A file nobody owns that does not exist yet can be claimed with claim_new_file. Never "
        + "bend an interface contract on your own: a contract that is wrong is a request.\n"
        + "5. When a colleague asks you for a change you are told, and you must answer before you edit, commit or merge "
        + "anything: respond_change accepts it (then make the change, build, team_commit, team_merge, and call change_done) "
        + "or rejects it with reasons. Rejecting is a legitimate answer.\n"
        + $"6. When every file you own meets its acceptance criteria, everything is merged and no request involving you is "
        + $"still open, call {PipelineStageTools.BuildSubmitToolName} once. Work that is not merged is lost when the run ends.\n"
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

    /// <summary>
    /// The first message of an owner that had finished and is called back because a colleague needs something from it.
    /// It is a new conversation - what it knows of its own work is in its checkout - so it gets the design again, and
    /// the request, in full, with what to do about it.
    /// </summary>
    /// <param name="requests">What waits for it: requests to answer, and any the lead ruled it must carry out.</param>
    public static string OwnerWakeOpening(
        string goal, string? approvedPlan, DesignPlan design, string name, IReadOnlyList<string> files, IReadOnlyList<ChangeRequest> requests)
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
        text.AppendLine("You had finished building them, and your checkout has what you built. A colleague now needs something from "
                        + "you, and the run waits for it:");
        text.AppendLine();
        foreach (var request in requests)
        {
            text.AppendLine(request.Binding ? ChangeRequestMessages.BindingNotice(request) : ChangeRequestMessages.Incoming(request));
            text.AppendLine();
        }

        text.Append("A request waiting for your answer needs respond_change first - accepting means you will make the change; "
                    + "rejecting needs reasons. Where the lead has ruled that you must make a change, make it. For each change "
                    + "you make: edit your own files, build, team_commit, team_sync if the team branch has moved, team_merge, "
                    + $"then change_done. When every request is dealt with, call {PipelineStageTools.BuildSubmitToolName} again.");
        return text.ToString();
    }

    /// <summary>The most characters of diff a judge is shown; a change bigger than this is judged on what fits, and the judge reads
    /// the rest of the code through its tools.</summary>
    public const int MaxJudgeDiffChars = 30_000;

    /// <summary>
    /// A judge's system prompt: who it is, its charter, what it can do, what a verdict is and what makes a finding count. A judge
    /// that cannot show a finding is told not to report it - the validator sets aside an unverifiable one anyway, but an
    /// owner is never sent to repair something that was only suspected.
    /// </summary>
    /// <param name="writesTests">The judge may write tests, on its own branch, which are evidence and are never merged.</param>
    /// <param name="hasTestProject">The solution has a test project to write them in.</param>
    public static string Judge(string name, TeamRole role, TeamFindingSeverity threshold, bool writesTests, bool hasTestProject)
    {
        var level = threshold.ToString().ToLowerInvariant();
        var abilities = !writesTests
            ? "You change nothing: you read and report."
            : hasTestProject
                ? "You may write tests, and only tests, in the solution's test project, on your own branch: build_solution, run_tests and "
                  + "team_commit work there. What you commit is evidence that travels with a finding and is never merged. You have no "
                  + "shell and cannot edit production code."
                : "This solution has no test project, so you cannot write a test to prove a finding; a bug you cannot demonstrate is at most "
                  + "medium. You change nothing: you read and report.";
        return $"You are {name}, a judge in an Ultra Pipeline run in IDE++. {role.Charter}\n\n"
               + "The owners have built the design and merged it to the team branch, and the gate has checked that it builds and its tests "
               + "pass. Your job is to judge the result, as your charter says, and give one verdict. "
               + $"{abilities} Read the code with read_file, list_files and search_codebase: they read the team branch as it is now. "
               + "find_symbol, find_references and get_diagnostics use the IDE's index from the start of the run, so they may not show "
               + "the latest changes - trust the files over them.\n"
               + $"Call {PipelineStageTools.VerdictSubmitToolName} once. The verdict is \"pass\" when nothing at {level} severity or above "
               + "is wrong and \"fail\" when something is; a pass cannot carry a finding at that level and a fail needs one. Every medium or "
               + "high finding names its files, gives evidence, and quotes the code it is about exactly as it is in the file: a finding "
               + "whose quote is not found there is set aside as unverified and counts for nothing. Do not report what you cannot show, "
               + "and style a formatter would fix is low. You judge alone - other judges are looking at other things and you cannot see "
               + "their findings. The user is not available - never ask them anything. Nothing you write in the conversation is "
               + $"recorded - only {PipelineStageTools.VerdictSubmitToolName} is.";
    }

    /// <summary>
    /// A judge's first message: the goal, the design, what the gate found, what changed (fenced - the designer's, the owners' and
    /// git's words about a repository), where to look first if the orchestrator said, and - for the judge that answers them - the
    /// misuse cases by id.
    /// </summary>
    /// <param name="gateSummary">The gate's one-line description of the commit being judged.</param>
    /// <param name="diff">What changed since the run began, or since the last round.</param>
    /// <param name="round">From 2, the judge is told the owners have repaired the code and that the changes are the repair's.</param>
    /// <param name="earlier">This judge's own findings from the last round that were still open - its own only: judges never see
    /// each other's. It is asked to check each against the code as it is now and report again the ones that stand.</param>
    public static string JudgeOpening(
        string goal,
        string? approvedPlan,
        DesignPlan design,
        string gateSummary,
        string diff,
        IReadOnlyList<string> focusFiles,
        IReadOnlyList<string> misuseIds,
        int round = 1,
        IReadOnlyList<LedgerFinding>? earlier = null)
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
        text.AppendLine($"The gate: {gateSummary}.");
        text.AppendLine();
        if (round > 1)
        {
            text.AppendLine($"This is round {round}. The owners have repaired the code since the last round, and the changes below are "
                            + "what they changed since then: judge those first, and what they touch. The rest of the code you can read as before.");
            text.AppendLine();
            if (earlier is { Count: > 0 })
            {
                text.AppendLine("These findings of yours from the last round were still open. Check each against the code as it is now. "
                                + "Report again any that still stands - under the same title and category, so it is recognised as the same "
                                + "finding - and leave out any that is fixed:");
                text.AppendLine();
                text.AppendLine(DataFence.Wrap("earlier findings", RenderEarlierFindings(earlier), MaxEarlierFindingsChars));
                text.AppendLine();
            }
        }

        text.AppendLine(DataFence.Wrap("changes", string.IsNullOrWhiteSpace(diff) ? "(no changes)" : diff, MaxJudgeDiffChars));
        text.AppendLine();
        if (focusFiles.Count > 0)
        {
            text.AppendLine($"Look at these files first: {string.Join(", ", focusFiles)}.");
            text.AppendLine();
        }

        if (misuseIds.Count > 0)
        {
            text.AppendLine($"Answer every misuse case in 'coverage', one entry each: {string.Join(", ", misuseIds)}.");
            text.AppendLine();
        }

        text.Append($"Judge the work, then call {PipelineStageTools.VerdictSubmitToolName}.");
        return text.ToString();
    }

    private const int MaxEarlierFindingsChars = 6_000;

    private static string RenderEarlierFindings(IReadOnlyList<LedgerFinding> findings)
    {
        var text = new System.Text.StringBuilder();
        foreach (var entry in findings)
        {
            var finding = entry.Finding;
            text.AppendLine($"- {entry.Id} [{finding.Severity.ToString().ToLowerInvariant()}] {finding.Title}");
            text.AppendLine($"  category: {finding.Category}; files: {(finding.Files.Count == 0 ? "(none)" : string.Join(", ", finding.Files))}");
            if (!string.IsNullOrWhiteSpace(finding.Quote))
            {
                text.AppendLine($"  quote: {finding.Quote.Trim()}");
            }
        }

        return text.ToString().TrimEnd();
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
