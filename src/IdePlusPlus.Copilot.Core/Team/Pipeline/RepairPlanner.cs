namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>A finding handed to an owner to repair, with what the owner is told to do about it: the lead's instruction when the lead
/// decided, otherwise the judge's own.</summary>
public sealed record RepairTask(LedgerFinding Finding, string Instruction);

/// <summary>What the runner decided about the findings that block a pass, by rule - <paramref name="Decided"/> - and the ones it
/// cannot place without judgement, which go to the orchestrator.</summary>
public sealed record RepairPlacement(IReadOnlyList<TriageDecision> Decided, IReadOnlyList<LedgerFinding> Hard);

/// <summary>
/// Triage's rules. Most findings need no judgement: one file, one owner, and the judge has said what would put it right - the owner
/// is handed it. A finding that is outside the goal goes to the user. What is left is for the orchestrator: a finding that spans
/// more than one owner's files, or none, one the judge says is a flaw in the design, and one that has already been repaired
/// once and came back, which must not be handed to the same owner with the same words again.
/// </summary>
public static class RepairPlanner
{
    public const string DeferReason = "The judge that found it said it is outside what this goal asked for.";
    public const string AssignReason = "Exactly one owner owns the file it is about.";

    public static RepairPlacement Place(IReadOnlyList<LedgerFinding> blocking, OwnershipMap ownership)
    {
        var decided = new List<TriageDecision>();
        var hard = new List<LedgerFinding>();
        foreach (var entry in blocking)
        {
            var finding = entry.Finding;
            if (finding.FixBy == FindingFixBy.User)
            {
                decided.Add(new TriageDecision(entry.Id, TriageAction.Defer, DeferReason, NextPrompt: NextPromptFor(finding)));
                continue;
            }

            if (entry.Status != FindingStatus.Open || finding.FixBy == FindingFixBy.Design)
            {
                hard.Add(entry);
                continue;
            }

            var owners = finding.Files
                .Select(ownership.OwnerOf)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (owners.Count == 1)
            {
                decided.Add(new TriageDecision(entry.Id, TriageAction.Assign, AssignReason, owners[0], InstructionFor(finding)));
            }
            else
            {
                hard.Add(entry);
            }
        }

        return new RepairPlacement(decided, hard);
    }

    /// <summary>The decisions of the rules and of the orchestrator, in the order the findings were given.</summary>
    public static IReadOnlyList<TriageDecision> Combine(
        IReadOnlyList<LedgerFinding> order, IEnumerable<TriageDecision> byRule, IEnumerable<TriageDecision> byLead)
    {
        var positions = order.Select((f, index) => (f.Id, index)).ToDictionary(p => p.Id, p => p.index, StringComparer.OrdinalIgnoreCase);
        return [.. byRule.Concat(byLead).OrderBy(d => positions.GetValueOrDefault(d.FindingId, int.MaxValue))];
    }

    /// <summary>
    /// Each owner's findings, from the plan. A finding assigned to an owner whose files it is not about is not the planner's to
    /// judge - <see cref="TriageValidator"/> has already - and one whose id the ledger does not know is left out.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<RepairTask>> TasksByOwner(TriagePlan plan, FindingLedger ledger)
    {
        var tasks = new Dictionary<string, List<RepairTask>>(StringComparer.Ordinal);
        foreach (var decision in plan.Assignments)
        {
            if (decision.Owner is not { } owner || ledger.Find(decision.FindingId) is not { } finding)
            {
                continue;
            }

            if (!tasks.TryGetValue(owner, out var list))
            {
                tasks[owner] = list = [];
            }

            list.Add(new RepairTask(finding, decision.Instruction ?? InstructionFor(finding.Finding)));
        }

        return tasks.ToDictionary(p => p.Key, p => (IReadOnlyList<RepairTask>)p.Value, StringComparer.Ordinal);
    }

    /// <summary>What a judge said would put a finding right - or, when it said nothing usable, to fix what it found.</summary>
    public static string InstructionFor(JudgeFinding finding)
    {
        var fix = PipelineText.Clean(finding.Fix);
        var text = fix.Length >= TriageValidator.MinInstructionLength ? fix : $"Fix this: {PipelineText.Clean(finding.Title)}";
        return PipelineText.Truncate(text, TriageValidator.MaxInstructionLength, out _).TrimEnd();
    }

    /// <summary>A request the user could run later to do what a deferred finding calls for.</summary>
    public static string NextPromptFor(JudgeFinding finding)
    {
        var title = PipelineText.Clean(finding.Title);
        var fix = PipelineText.Clean(finding.Fix);
        var text = fix.Length == 0 ? title : $"{title}. {fix}";
        if (finding.Files.Count > 0)
        {
            text = $"In {string.Join(", ", finding.Files)}: {text}";
        }

        return PipelineText.Truncate(text, TriageValidator.MaxNextPromptLength, out _).TrimEnd();
    }
}
