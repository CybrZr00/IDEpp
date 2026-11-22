namespace IdePlusPlus.Copilot.Core.Team.Pipeline;

/// <summary>What the orchestrator decides for a finding it was asked to triage.</summary>
public enum TriageAction
{
    /// <summary>An owner repairs it, with the orchestrator's instruction.</summary>
    Assign,

    /// <summary>It is not a real problem, or not worth what fixing it costs.</summary>
    Dismiss,

    /// <summary>It is real but outside this goal: it goes to the user as a suggested next prompt.</summary>
    Defer,
}

/// <summary>One decision as the orchestrator submits it. Everything is text, because the other end is a
/// language model: <see cref="Action"/> is "assign", "dismiss" or "defer" and is read leniently.</summary>
public sealed record TriageItem(
    string? Finding,
    string? Action,
    string? Owner = null,
    string? Instruction = null,
    string? Reason = null,
    string? NextPrompt = null);

/// <summary>What the orchestrator submits to triage: a decision for each finding it was given.</summary>
public sealed record TriageSubmission(IReadOnlyList<TriageItem>? Decisions, string? Notes = null);

/// <summary>A checked decision. For <see cref="TriageAction.Assign"/> the owner and instruction are set;
/// for <see cref="TriageAction.Defer"/> the next prompt is.</summary>
public sealed record TriageDecision(
    string FindingId,
    TriageAction Action,
    string Reason,
    string? Owner = null,
    string? Instruction = null,
    string? NextPrompt = null);

/// <summary>The orchestrator's triage, checked against the findings it was given and the owners the
/// design named. A finding can be assigned to several owners - one decision each, when it spans their
/// files - but a dismissed or deferred finding has exactly one decision.</summary>
public sealed record TriagePlan(IReadOnlyList<TriageDecision> Decisions, string? Notes = null)
{
    /// <summary>A plan that decides nothing: what a triage the orchestrator could not give amounts to.</summary>
    public static TriagePlan Empty { get; } = new([]);

    public IReadOnlyList<TriageDecision> Assignments => [.. Decisions.Where(d => d.Action == TriageAction.Assign)];

    public IReadOnlyList<TriageDecision> Dismissed => [.. Decisions.Where(d => d.Action == TriageAction.Dismiss)];

    public IReadOnlyList<TriageDecision> Deferred => [.. Decisions.Where(d => d.Action == TriageAction.Defer)];

    /// <summary>The decisions for one finding.</summary>
    public IReadOnlyList<TriageDecision> For(string findingId) =>
        [.. Decisions.Where(d => string.Equals(d.FindingId, findingId, StringComparison.OrdinalIgnoreCase))];

    /// <summary>What <paramref name="owner"/> is to repair, in the order the findings were given.</summary>
    public IReadOnlyList<TriageDecision> AssignedTo(string owner) =>
        [.. Assignments.Where(d => string.Equals(d.Owner, owner, StringComparison.OrdinalIgnoreCase))];
}

public static class TriageValidator
{
    public const int MaxDecisions = 60;
    public const int MinInstructionLength = 10;
    public const int MinReasonLength = 20;
    public const int MaxInstructionLength = 600;
    public const int MaxReasonLength = 400;
    public const int MaxNextPromptLength = 400;
    public const int MaxNotesLength = 600;

    /// <summary>
    /// Checks a triage against <paramref name="offered"/>, the findings the orchestrator was shown. Every
    /// one must be decided; an owner a finding is assigned to must be one of the design's owners and, where
    /// the finding names files someone owns, must own one of them - an owner may edit only its own files,
    /// so a repair handed to anyone else could only end in a request. Dismissing and deferring need a
    /// reason, deferring also a prompt for the user. The reasons for a refusal are written to be returned
    /// to the orchestrator as they are.
    /// </summary>
    public static PipelineCheck<TriagePlan> Validate(
        TriageSubmission? submission, IReadOnlyList<LedgerFinding> offered, OwnershipMap ownership)
    {
        if (submission?.Decisions is not { Count: > 0 } items)
        {
            return PipelineCheck<TriagePlan>.Failed(["The triage has no decisions: decide every finding you were given."]);
        }

        var errors = new ErrorList();
        if (items.Count > MaxDecisions)
        {
            errors.Add($"{items.Count} decisions is too many; there are only {offered.Count} finding(s) to decide.");
        }

        var ids = string.Join(", ", offered.Select(f => f.Id));
        var positions = offered.Select((f, position) => (f.Id, position)).ToDictionary(p => p.Id, p => p.position, StringComparer.OrdinalIgnoreCase);
        var owners = ownership.Owners;
        var decisions = new List<(int Order, TriageDecision Decision)>();
        foreach (var (item, index) in items.Take(MaxDecisions).Select((item, index) => (item, index)))
        {
            var at = PipelineText.Item("decisions", index);
            if (item is null)
            {
                errors.Add($"{at} is empty.");
                continue;
            }

            var id = PipelineText.Clean(item.Finding);
            var finding = offered.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));
            if (finding is null)
            {
                errors.Add($"{at}: '{id}' is not one of the findings you were given ({ids}).");
                continue;
            }

            if (ParseAction(item.Action) is not { } action)
            {
                errors.Add($"{at} ({finding.Id}): action '{PipelineText.Clean(item.Action)}' is not one of assign, dismiss, defer.");
                continue;
            }

            var reason = Cut(item.Reason, MaxReasonLength);
            var order = positions[finding.Id];
            switch (action)
            {
                case TriageAction.Assign:
                    var decision = Assign(at, finding, item, reason, ownership, owners, errors);
                    if (decision is not null)
                    {
                        decisions.Add((order, decision));
                    }

                    break;

                case TriageAction.Dismiss:
                    if (reason.Length < MinReasonLength)
                    {
                        errors.Add($"{at} ({finding.Id}): dismissing needs a reason - why this is not a problem or not worth fixing "
                                   + $"(at least {MinReasonLength} characters).");
                        break;
                    }

                    decisions.Add((order, new TriageDecision(finding.Id, TriageAction.Dismiss, reason)));
                    break;

                default:
                    var nextPrompt = Cut(item.NextPrompt, MaxNextPromptLength);
                    if (reason.Length < MinReasonLength)
                    {
                        errors.Add($"{at} ({finding.Id}): deferring needs a reason - why it is outside this goal "
                                   + $"(at least {MinReasonLength} characters).");
                    }
                    else if (nextPrompt.Length < MinInstructionLength)
                    {
                        errors.Add($"{at} ({finding.Id}): deferring needs a nextPrompt - a request the user could run later to do it.");
                    }
                    else
                    {
                        decisions.Add((order, new TriageDecision(finding.Id, TriageAction.Defer, reason, NextPrompt: nextPrompt)));
                    }

                    break;
            }
        }

        foreach (var group in decisions.GroupBy(d => d.Decision.FindingId, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1 && group.Any(d => d.Decision.Action != TriageAction.Assign))
            {
                errors.Add($"{group.Key} is decided more than once, and not only by assigning it. A finding is either assigned "
                           + "(to one or more owners), dismissed, or deferred - one of those.");
            }
            else if (group.Select(d => d.Decision.Owner).Distinct(StringComparer.OrdinalIgnoreCase).Count() < group.Count())
            {
                errors.Add($"{group.Key} is assigned to the same owner more than once; give that owner one instruction.");
            }
        }

        foreach (var missing in offered.Where(f => !items.Any(i => string.Equals(PipelineText.Clean(i?.Finding), f.Id, StringComparison.OrdinalIgnoreCase))))
        {
            errors.Add($"{missing.Id} has no decision: assign it, dismiss it, or defer it.");
        }

        var notes = Cut(submission.Notes, MaxNotesLength);
        return errors.ResultOr(() => new TriagePlan(
            [.. decisions.OrderBy(d => d.Order).Select(d => d.Decision)],
            notes.Length == 0 ? null : notes));
    }

    private static TriageDecision? Assign(
        string at,
        LedgerFinding finding,
        TriageItem item,
        string reason,
        OwnershipMap ownership,
        IReadOnlyList<string> owners,
        ErrorList errors)
    {
        var owner = PipelineText.Clean(item.Owner).ToLowerInvariant();
        var instruction = Cut(item.Instruction, MaxInstructionLength);
        var ok = true;
        if (!owners.Contains(owner, StringComparer.Ordinal))
        {
            errors.Add($"{at} ({finding.Id}): '{owner}' is not an owner ({string.Join(", ", owners)}).");
            ok = false;
        }
        else
        {
            var owned = finding.Finding.Files
                .Select(file => (File: file, Owner: ownership.OwnerOf(file)))
                .Where(f => f.Owner is not null)
                .ToList();
            if (owned.Count > 0 && !owned.Any(f => string.Equals(f.Owner, owner, StringComparison.Ordinal)))
            {
                errors.Add($"{at} ({finding.Id}): {owner} owns none of its files ("
                           + string.Join(", ", owned.Select(f => $"{f.File} is {f.Owner}'s"))
                           + "), and an owner may edit only its own files. Assign it to one of them.");
                ok = false;
            }
        }

        if (instruction.Length < MinInstructionLength)
        {
            errors.Add($"{at} ({finding.Id}): assigning needs an instruction - what the owner is to change.");
            ok = false;
        }

        return ok ? new TriageDecision(finding.Id, TriageAction.Assign, reason, owner, instruction) : null;
    }

    /// <summary>"assign"/"repair"/"fix", "dismiss"/"drop"/"ignore", "defer"/"later"/"user"; null for the rest.</summary>
    internal static TriageAction? ParseAction(string? action) =>
        PipelineText.Clean(action).ToLowerInvariant() switch
        {
            "assign" or "repair" or "fix" => TriageAction.Assign,
            "dismiss" or "drop" or "ignore" => TriageAction.Dismiss,
            "defer" or "later" or "user" => TriageAction.Defer,
            _ => null,
        };

    private static string Cut(string? text, int max) =>
        PipelineText.Truncate(DataFence.Strip(PipelineText.Clean(text)), max, out _).TrimEnd();
}
