using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

// Triage and repair: the findings that keep a round from passing are placed with the owners of the files they are about, and the
// owners - the same agents, in the same checkouts, that built those files - repair them.
public sealed partial class UltraPipelineRun
{
    private const int RepairIterations = 80;

    /// <summary>The findings that went to the user instead of an owner, as triage decided them: each with the request the user
    /// could run later to do it. The report lists them.</summary>
    private readonly List<TriageDecision> _deferred = [];

    // What this round's repair did, for the history of the loop: what each owner reported, and what the lead dismissed or left to the
    // user. Set afresh by each repair round.
    private IReadOnlyList<RepairNote> _repairNotes = [];
    private List<string> _roundDismissed = [];
    private List<string> _roundDeferred = [];

    private string TitleOf(string findingId) => _ledger.Find(findingId)?.Finding.Title ?? findingId;

    /// <summary>
    /// One repair round: triage decides what each owner is to repair, and each owner that was given something does it, in
    /// parallel, in its own worktree. Whether the round did anything is whether the team branch moved: a repair that merged
    /// nothing is not worth judging again.
    /// </summary>
    private async Task<RepairOutcome> RepairStageAsync(
        int round, string goal, string? approvedPlan, RunWorkspace workspace, CancellationToken cancellationToken)
    {
        _repairNotes = [];
        _roundDismissed = [];
        _roundDeferred = [];
        var threshold = _options.PipelineSeverityThreshold;
        if (RepairRunner is { } stand)
        {
            return await stand(round, _ledger.Repairable(threshold), cancellationToken);
        }

        var tasks = await TriageStageAsync(round, goal, _ledger.Blocking(threshold), cancellationToken);
        if (tasks.Count == 0)
        {
            return new RepairOutcome(false, "no finding could be handed to an owner");
        }

        var design = _design!;
        var findingCount = tasks.Values.Sum(t => t.Count);
        SetStage($"Repair - {tasks.Count} owner{(tasks.Count == 1 ? "" : "s")} fixing {findingCount} finding{(findingCount == 1 ? "" : "s")} (round {round})");
        using var clock = _budget.BeginStage(UltraStage.Repair, round);

        var teamHead = await workspace.TeamHeadAsync(cancellationToken);
        var work = new WorkContext(clock, workspace, goal, approvedPlan, design, cancellationToken) { Repairs = tasks, Round = round };
        var running = _owners.Where(o => tasks.ContainsKey(o.Name)).ToList();
        var mergesBefore = running.ToDictionary(o => o.Name, o => o.State.Merges, StringComparer.Ordinal);
        var requestsBefore = _changes.All.Count;
        foreach (var owner in _owners)
        {
            owner.BeginStage();
        }

        foreach (var owner in running)
        {
            owner.IsRunning = true; // before any of them starts, as in the build
        }

        lock (_wakeLock)
        {
            _serving = work;
        }

        List<PipelineAgentOutcome> outcomes;
        IReadOnlyList<PipelineAgentOutcome> calledBack;
        try
        {
            outcomes = [.. await Task.WhenAll(running.Select(owner => RunOwnerAsync(owner, work)))];
            calledBack = await DrainWakesAsync(work);
        }
        finally
        {
            lock (_wakeLock)
            {
                _serving = null;
            }
        }

        foreach (var agent in _changes.All.Where(r => r.IsOpen).SelectMany(r => new[] { r.From, r.To }).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            _changes.CancelAllFor(agent, "The repair stage ended before it was settled.");
        }

        var records = new List<RepairOwnerRecord>();
        for (var i = 0; i < running.Count; i++)
        {
            var owner = running[i];
            if (outcomes[i].Gap is { } gap)
            {
                Gap(gap);
            }

            if (owner.LeftUnmerged is { } unmerged)
            {
                Gap($"{owner.Name} reported with {unmerged}: that work is on its branch, not the team branch.");
            }

            records.Add(new RepairOwnerRecord(
                owner.Name,
                [.. tasks[owner.Name].Select(t => t.Finding.Id)],
                owner.Submitted,
                owner.Report?.Summary,
                owner.Report?.Unmet ?? [],
                owner.State.Merges - mergesBefore[owner.Name],
                owner.State.Steps,
                outcomes[i].Gap));
        }

        _repairNotes = [.. records.Select(r => new RepairNote(r.Owner, r.Findings, r.Summary, r.Unmet))];
        var changed = await workspace.TeamHeadAsync(cancellationToken) != teamHead;
        await RecordAsync($"loop-{round}/repairs.json", new RepairRecord(round, changed, records), cancellationToken);
        await RecordAsync($"loop-{round}/change-requests.json", new ChangeRequestsRecord([.. _changes.All.Skip(requestsBefore)]), cancellationToken);

        var merges = records.Sum(r => r.Merges);
        LogStage(
            clock, UltraStage.Repair, round, [.. outcomes, .. calledBack],
            $"{merges} merge(s) from {running.Count} owner(s)" + (_changes.All.Count > requestsBefore ? $", {_changes.All.Count - requestsBefore} change request(s)" : ""));
        return new RepairOutcome(changed, changed ? null : "no owner merged anything");
    }

    /// <summary>
    /// Decides what each owner is to repair. The rules place what they can (<see cref="RepairPlanner"/>); the orchestrator is asked
    /// only about the rest - a finding that spans owners' files, one about the design, one that came back after a repair - and if it
    /// cannot decide those they stay as they were: open, with nobody to repair them. What it dismisses or defers stops blocking;
    /// what is deferred is kept for the report.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<RepairTask>>> TriageStageAsync(
        int round, string goal, IReadOnlyList<LedgerFinding> blocking, CancellationToken cancellationToken)
    {
        var placed = RepairPlanner.Place(blocking, _ownership);
        var byLead = TriagePlan.Empty;
        var asked = false;
        var decided = true;
        string? notes = null;
        if (placed.Hard.Count > 0 && _conductor is { } lead)
        {
            asked = true;
            SetStage($"Triage - the lead is deciding {placed.Hard.Count} finding{(placed.Hard.Count == 1 ? "" : "s")} (round {round})");
            using var clock = _budget.BeginStage(UltraStage.Triage, round);
            var outcome = await lead.TriageAsync(goal, placed.Hard, _ownership, clock, cancellationToken);
            byLead = outcome.Plan;
            decided = outcome.Decided;
            notes = outcome.Note ?? outcome.Plan.Notes;
            if (!outcome.Decided && outcome.Note is { } why)
            {
                Gap(why);
            }

            LogStage(clock, UltraStage.Triage, round, [], decided ? $"{outcome.Plan.Decisions.Count} decision(s)" : "no decision");
        }

        var plan = new TriagePlan(RepairPlanner.Combine(blocking, placed.Decided, byLead.Decisions), notes);
        foreach (var dismissed in plan.Dismissed)
        {
            _ledger.SetStatus(dismissed.FindingId, FindingStatus.Dismissed, $"dismissed: {dismissed.Reason}");
            _roundDismissed.Add($"{TitleOf(dismissed.FindingId)} ({dismissed.Reason})");
        }

        foreach (var deferred in plan.Deferred)
        {
            _ledger.SetStatus(deferred.FindingId, FindingStatus.Dismissed, $"for the user: {deferred.Reason}");
            _deferred.Add(deferred);
            _roundDeferred.Add(TitleOf(deferred.FindingId));
        }

        await RecordAsync($"loop-{round}/triage.json", new TriageRecord(round, plan.Decisions, asked, decided, notes), cancellationToken);
        return RepairPlanner.TasksByOwner(plan, _ledger);
    }
}
