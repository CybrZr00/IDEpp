using System.IO;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Team.Pipeline;

// The gate: the deterministic build, tests and conformity check of the team branch, before and between the judges.
public sealed partial class UltraPipelineRun
{
    /// <summary>
    /// The check that the solution builds as it stands, before any agent has been given a model call. A solution that does not
    /// build cannot have anything merged into it - every merge builds first - so a team started on one would spend its whole
    /// budget failing the same build; the user gets the build's own report instead.
    /// </summary>
    /// <exception cref="UltraTeamPreflightException">The solution does not build.</exception>
    private async Task PreflightBuildAsync(RunWorkspace workspace, CancellationToken cancellationToken)
    {
        _checkingBuild = true;
        SetSetup("Checking that the solution builds in the new workspace, before the agents start");
        try
        {
            var build = await _gate!.BuildAsync(workspace.Start, cancellationToken);
            if (!build.Ok)
            {
                throw new UltraTeamPreflightException(build.Report, workspace.NotInTheCopy(build.Report, _gate.Worktree!.Path));
            }

            workspace.RememberBuiltWorktree(_gate.Worktree!.Path);
        }
        finally
        {
            _checkingBuild = false;
        }
    }

    /// <summary>
    /// Builds and tests the team branch as the owners left it, and checks what they changed for conformity. The result is
    /// recorded for the round, with the findings it amounts to - a build error or a failed test is high, a style note is low -
    /// ready for the ledger. It never changes the team branch: the conformity check's rewrites are made in the gate's own
    /// detached checkout, which is put back afterwards.
    /// </summary>
    private async Task<GateResult> GateStageAsync(RunWorkspace workspace, int round, CancellationToken cancellationToken)
    {
        SetStage(round > 1 ? $"Gate - checking the repairs (round {round})" : "Gate - building and testing what the owners merged");
        using var clock = _budget.BeginStage(UltraStage.Gate, round);

        var head = await workspace.TeamHeadAsync(cancellationToken);
        var changed = await _teamGit.GetChangedFilesAsync(workspace.RepositoryRoot, workspace.Snapshot, head, cancellationToken);
        var request = new GateRequest(
            head,
            _testProjectPaths,
            [.. changed
                .Where(c => c.Kind != TeamFileChangeKind.Deleted)
                .Select(c => workspace.ToSolutionPath(c.RelativePath))
                .OfType<string>()
                .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))]);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clock.HardStop);
        GateResult result;
        try
        {
            result = GateRunner is { } stand
                ? await stand(request, limit.Token)
                : await _gate!.RunAsync(request, limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The run ran out of time inside the gate. What it could not check it could not pass.
            const string stopped = "The gate was stopped because the run ran out of time.";
            result = new GateResult(
                head, false, stopped, [], TestsRan: false, 0, 0, 0, [], stopped, GateConformity.NotRun(stopped));
        }

        var findings = GateFindings.From(result);
        await RecordAsync($"loop-{round}/gate.json", new GateRecord(result, [.. findings.Select(f => f.Finding)]), cancellationToken);
        if (!result.Ok)
        {
            Gap($"The gate failed in round {round}: {result.Describe()}.");
        }

        LogStage(clock, UltraStage.Gate, round, [], result.Describe());
        return result;
    }
}
