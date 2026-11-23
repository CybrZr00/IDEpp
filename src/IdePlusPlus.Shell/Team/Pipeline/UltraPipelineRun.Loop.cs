using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>What a repair round did: whether anything reached the team branch, and a line saying what, for the record.</summary>
internal sealed record RepairOutcome(bool Changed, string? Note = null);

// The loop: gate, judges, and - while the judges are not satisfied - a repair, up to the cap and always ending on a judging round,
// so what lands was always judged.
public sealed partial class UltraPipelineRun
{
    /// <summary>Everything the judges and the gate have found, across the rounds of this run.</summary>
    private readonly FindingLedger _ledger = new();

    private readonly ConvergenceTracker _convergence = new();

    /// <summary>
    /// What a repair round is, for tests that are about the loop and not about the repair: given the round and the findings the
    /// owners can still be asked to fix, it changes the team branch (or not) and says so. When it is not set the round is the real
    /// thing - triage, then the owners - which is <see cref="RepairStageAsync"/>.
    /// </summary>
    internal Func<int, IReadOnlyList<LedgerFinding>, CancellationToken, Task<RepairOutcome>>? RepairRunner { get; init; }

    /// <summary>
    /// The rounds. Each is the gate and then, if the gate is green, the judges; what they find goes into the ledger, which is what
    /// decides the round: it passes when the gate is green and nothing at or above the severity threshold is still open. A round
    /// that does not pass is followed by a repair, until the cap on repairs is used - the last round is judged and not repaired - or
    /// the loop is not getting anywhere: the score of what is open has not fallen for two rounds, or a repair changed nothing, in
    /// which case judging the same code again would only find the same things. A red gate skips the judges: a build that fails
    /// is not worth their time, and its errors go to the owners like any finding.
    /// </summary>
    private async Task<UltraTeamEndReason> JudgeLoopAsync(
        string goal, string? approvedPlan, RunWorkspace workspace, WorkspaceWorktree readOnly, CancellationToken cancellationToken)
    {
        var threshold = _options.PipelineSeverityThreshold;
        var lastRound = Math.Max(0, _options.PipelineMaxLoops) + 1;
        var judgedFrom = workspace.Snapshot; // What the judges are shown the changes since: the start, then the last code they saw.

        for (var round = 1; ; round++)
        {
            var gate = await GateStageAsync(workspace, round, cancellationToken);
            _ledger.RecordRound(round, GateFindings.Judge, GateFindings.From(gate));
            _ledger.CompleteRound(round, [GateFindings.Judge]);

            if (gate.Ok)
            {
                if (Spent() is { } spent)
                {
                    return spent;
                }

                var judged = await JudgeStageAsync(goal, approvedPlan, workspace, readOnly, gate, judgedFrom, round, cancellationToken);
                judgedFrom = judged.Commit;
                var answered = judged.Reports.Where(r => r.Verdict is not null).ToList();
                foreach (var report in answered)
                {
                    _ledger.RecordRound(round, report.Agent.Name, report.Verdict!.Findings);
                }

                _ledger.CompleteRound(round, answered.Select(r => r.Agent.Name));
            }

            var blocking = _ledger.Blocking(threshold);
            var passed = gate.Ok && blocking.Count == 0;
            var score = ConvergenceTracker.Score(blocking);
            var stalled = _convergence.Observe(score);
            await RecordAsync($"loop-{round}/findings.json", new FindingsRecord(round, passed, score, stalled, _ledger.Findings), cancellationToken);

            if (passed)
            {
                return UltraTeamEndReason.AllDone; // Judged and passed: the run is done, whatever is left of the budget.
            }

            if (_convergence.NoProgress)
            {
                Gap($"The loop stopped after round {round}: what is open has not shrunk in {stalled} rounds in a row "
                    + $"(score {string.Join(", ", _convergence.History)}), so more repairs are not likely to help.");
                return UltraTeamEndReason.NoProgress;
            }

            if (round >= lastRound)
            {
                return UltraTeamEndReason.LoopCapReached;
            }

            if (Spent() is { } outOfBudget)
            {
                return outOfBudget;
            }

            var repair = await RepairStageAsync(round, goal, approvedPlan, workspace, cancellationToken);
            if (!repair.Changed)
            {
                // Triage may have cleared everything that was open - dismissed it, or deferred it to the user - and then there is
                // nothing to judge again: the code is as the judges saw it, and what they said stands, less what was cleared.
                if (gate.Ok && _ledger.Blocking(threshold).Count == 0)
                {
                    await RecordAsync($"loop-{round}/findings.json", new FindingsRecord(round, true, 0, stalled, _ledger.Findings), cancellationToken);
                    return UltraTeamEndReason.AllDone;
                }

                Gap($"The loop stopped after round {round}: the repair changed nothing{(repair.Note is { Length: > 0 } note ? $" ({note})" : "")}, "
                    + "so judging the same code again would find the same things.");
                return UltraTeamEndReason.NoProgress;
            }
        }
    }
}
