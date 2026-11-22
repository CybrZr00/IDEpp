using System.Text;

namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>
/// What the orchestrator is told at each of its three decisions. The system prompts are the run's own
/// words; everything in an opening that another agent wrote - a judge's finding, an owner's refusal, the
/// design - is fenced as data (<see cref="DataFence"/>), because a finding quotes the repository and the
/// repository is where an attacker's sentence would sit.
///
/// Every system prompt opens "You are lead, " - the pipeline's convention, which the test client reads
/// the agent's name from.
/// </summary>
public static class ConductorPrompts
{
    /// <summary>The most files listed for one owner; the rest are counted.</summary>
    private const int MaxFilesListedPerOwner = 30;

    private const string Intro =
        "You are lead, the orchestrator of an Ultra Pipeline run in IDE++. You write no code and have no worktree. "
        + "The pipeline works in stages - surveyors read the code, one designer plans, owners build and repair, "
        + "judges check the result - and you make the few decisions that need judgement. The user gave one goal and "
        + "is not available: never ask them anything. The code you can read is as it stood when the current stage "
        + "began; work still in progress is not in it.";

    public static string Roster(RosterLimits limits)
    {
        var judges = string.Join("; ", PipelineRoles.StandardJudges.Select(j => $"{j.Name} {j.Role.DisplayName}"));
        var surveyors = limits.MaxUnderstand > 0
            ? $"At most {limits.MaxUnderstand} surveyor(s)"
            : "No surveyors are allowed in this run: leave the list empty";
        var custom = limits.MaxCustomJudges > 0
            ? $"At most {limits.MaxCustomJudges} custom judge(s)"
            : "No custom judges are allowed in this run: leave the list empty";

        return $"{Intro}\n\n"
               + "Your decision now is the roster. Read enough of the code to see what the goal touches (read_file, "
               + "list_files, search_codebase, find_symbol), then call lead_submit_roster once.\n\n"
               + "Surveyors (u1, u2, ...) are read-only agents that each survey one area before anything is designed: what "
               + "already exists that the goal can reuse, what is missing, and what could be trimmed. The designer plans "
               + $"from their reports. {surveyors}. Give each its own area and the question that matters there. Fewer is "
               + "better: a small or well-contained goal needs none, and then the designer surveys for itself; two "
               + "surveyors on one area cost twice for the answer of one.\n\n"
               + $"The standard judges check the finished work: {judges}. {custom}. Add one only for a risk those four "
               + "would not look at in particular - a protocol, a data-migration rule, accessibility, a performance "
               + $"budget. Give it a title, a charter (what it checks and what a failure looks like, under "
               + $"{PipelineRoles.MaxCustomCharterLength} characters) and a scope: \"none\" reads and reports, "
               + "\"testsOnly\" may also write failing tests. A judge never edits the code it judges. Do not repeat a "
               + "standard judge.\n\n"
               + "A roster over the limits is sent back. An empty roster is a decision too.";
    }

    public static string RosterOpening(string goal, string? approvedPlan)
    {
        var plan = string.IsNullOrWhiteSpace(approvedPlan)
            ? ""
            : $"Approved plan (the user approved these steps):\n{approvedPlan.Trim()}\n\n";
        return $"Goal:\n{goal.Trim()}\n\n{plan}Decide the roster, then call lead_submit_roster.";
    }

    public static string Triage() =>
        $"{Intro}\n\n"
        + "The judges have reported, and the owners repair what they found. Most findings need no judgement - one file, "
        + "one owner - and the runner hands those over itself. The ones below need yours: they span more than one "
        + "owner's files, nobody owns the file, an owner and a judge disagree, or a repair was tried and the finding "
        + "came back (escalated).\n\n"
        + "Decide every finding below, then call lead_submit_triage once.\n"
        + "- assign: an owner repairs it. Name the owner and say exactly what to change - the owner works from your "
        + "instruction, not from the judge's wording. An owner may edit only its own files, so the owner you name must "
        + "own one of the finding's files, and a finding that spans two owners' files is assigned to each of them, "
        + "with that owner's part.\n"
        + "- dismiss: it is not a real problem, or not worth what fixing it would cost. Say why.\n"
        + "- defer: it is real but outside what the goal asked for. Say why, and give a request the user could run "
        + "later to do it.\n"
        + "An escalated finding was repaired once and came back. Do not hand it back with the same instruction: "
        + "change the approach, split it between owners, or defer it. Read the code if a finding's evidence does "
        + $"not settle it.\n\n{DataFence.Instruction}";

    public static string TriageOpening(string goal, IReadOnlyList<LedgerFinding> findings, OwnershipMap ownership)
    {
        var text = new StringBuilder();
        text.AppendLine($"Goal:\n{goal.Trim()}\n");
        text.AppendLine(DataFence.Wrap("owners", DescribeOwners(ownership)));
        text.AppendLine();
        text.AppendLine(DataFence.Wrap("findings", string.Join("\n\n", findings.Select(f => DescribeFinding(f, ownership)))));
        text.AppendLine();
        text.Append("Decide every finding, then call lead_submit_triage.");
        return text.ToString();
    }

    public static string Arbitration() =>
        $"{Intro}\n\n"
        + "Two agents disagree about a file. One asked its owner to change it, the owner refused with reasons, and the "
        + "requester appealed. You decide, once: your ruling is final and neither may appeal it.\n"
        + "- requester: the owner must make the change. Rule this way when the change is needed for the goal or for the "
        + "design's contracts and the owner's reasons do not hold.\n"
        + "- owner: the refusal stands. Rule this way when the change is not needed, goes beyond the goal, or the "
        + "requester can get what it needs another way.\n"
        + "The owner knows its file, so its refusal carries weight: overrule it for a reason, not for convenience. "
        + "Either way, give the instruction whoever has to act will follow - for the requester's side, exactly what "
        + "the owner is to change; for the owner's, what the requester does instead. Read the file if the argument "
        + $"turns on what the code says. Then call lead_arbitrate.\n\n{DataFence.Instruction}";

    public static string ArbitrationOpening(string goal, ChangeRequest request, string? design)
    {
        var text = new StringBuilder();
        text.AppendLine($"Goal:\n{goal.Trim()}\n");
        if (!string.IsNullOrWhiteSpace(design))
        {
            text.AppendLine(DataFence.Wrap("design", design));
            text.AppendLine();
        }

        var appeal = new StringBuilder()
            .AppendLine($"{request.Id}: {request.From} asked {request.To} to change {request.File}.")
            .AppendLine($"Change asked for: {request.Change}")
            .AppendLine($"Why: {request.Rationale}")
            .AppendLine($"{request.To} refused: {request.Reasoning ?? "(no reasoning given)"}")
            .Append($"{request.From} appealed: {request.AppealArgument ?? "(no argument given)"}");
        text.AppendLine(DataFence.Wrap($"appeal {request.Id}", appeal.ToString()));
        text.AppendLine();
        text.Append($"Rule on {request.Id}, then call lead_arbitrate.");
        return text.ToString();
    }

    /// <summary>The owners and their files, as the orchestrator chooses among them.</summary>
    public static string DescribeOwners(OwnershipMap ownership)
    {
        var lines = new List<string>();
        foreach (var owner in ownership.Owners)
        {
            var files = ownership.FilesOf(owner);
            var listed = string.Join(", ", files.Take(MaxFilesListedPerOwner));
            lines.Add($"{owner}: {listed}{(files.Count > MaxFilesListedPerOwner ? $" (+{files.Count - MaxFilesListedPerOwner} more)" : "")}");
        }

        return lines.Count == 0 ? "(no owners)" : string.Join("\n", lines);
    }

    /// <summary>One finding for the orchestrator to decide: where it stands, who owns what it names, and
    /// the judge's own evidence.</summary>
    public static string DescribeFinding(LedgerFinding finding, OwnershipMap ownership)
    {
        var f = finding.Finding;
        var files = f.Files.Count == 0
            ? "(none named)"
            : string.Join(", ", f.Files.Select(file => $"{file} ({ownership.OwnerOf(file) ?? "no owner"})"));
        var text = new StringBuilder()
            .AppendLine($"{finding.Id} [{f.Severity.ToString().ToLowerInvariant()}, {finding.Status.ToString().ToLowerInvariant()}, "
                        + $"seen in {finding.SeenCount} loop(s)] {f.Category} - reported by {finding.Judge}")
            .AppendLine($"  Title: {f.Title}")
            .AppendLine($"  Files: {files}");
        if (f.Evidence is { Length: > 0 })
        {
            text.AppendLine($"  Evidence: {f.Evidence}");
        }

        if (f.Quote is { Length: > 0 })
        {
            text.AppendLine($"  Quote: {f.Quote}");
        }

        if (f.Fix is { Length: > 0 })
        {
            text.AppendLine($"  Suggested fix ({f.FixBy.ToString().ToLowerInvariant()}): {f.Fix}");
        }

        if (finding.Note is { Length: > 0 })
        {
            text.AppendLine($"  Note: {finding.Note}");
        }

        return text.ToString().TrimEnd();
    }
}
