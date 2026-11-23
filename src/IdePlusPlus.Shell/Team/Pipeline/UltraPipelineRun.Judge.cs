using System.IO;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Team.Pipeline;

/// <summary>One judge's answer for one round: the verdict that passed validation, or why there is none, and - for a judge that writes
/// tests - the branch its evidence is on.</summary>
internal sealed record JudgeReport(
    PipelineAgent Agent, ValidatedVerdict? Verdict, string? Unavailable, string? EvidenceBranch, string? EvidenceCommit)
{
    public string Title => Agent.Role.DisplayName;
}

/// <summary>What the judges said in one round, about the team branch at <paramref name="Commit"/>.</summary>
internal sealed record JudgeRound(int Round, string Commit, IReadOnlyList<JudgeReport> Reports)
{
    /// <summary>
    /// Every judge that gave a verdict has nothing at or above <paramref name="threshold"/> that still counts. A judge that gave none
    /// does not hold a round up - the round goes on without it, flagged - so a judge that cannot answer can never loop a run.
    /// </summary>
    public bool Passed(TeamFindingSeverity threshold) => Reports.All(r => r.Verdict is null || !r.Verdict.BlocksPass(threshold));

    public IReadOnlyList<JudgeReport> Unavailable => [.. Reports.Where(r => r.Verdict is null)];
}

// The judge stage: the standard four judges and any the orchestrator added each read what the owners built and give a verdict.
public sealed partial class UltraPipelineRun
{
    private const int JudgeIterations = 40;

    /// <summary>A verdict that keeps being refused is given up on after this many tries: a judge that cannot produce a valid one is
    /// recorded as unavailable instead of being asked until the budget is gone.</summary>
    private const int MaxVerdictRefusals = 3;

    /// <summary>One judge. A judge that writes tests keeps its worktree and branch for the whole run - its failing tests are evidence
    /// that travels with its findings - and the others have nothing of their own.</summary>
    private sealed class JudgeAgent(PipelineAgent agent, IReadOnlyList<string> focusFiles)
    {
        public PipelineAgent Agent { get; } = agent;

        public string Name => Agent.Name;

        public IReadOnlyList<string> FocusFiles { get; } = focusFiles;

        public bool WritesTests => Agent.Role.EditScope == TeamRoleEditScope.TestsOnly;

        public TeamWorkerState? State { get; set; }

        public WorkspaceWorktree? Worktree { get; set; }

        public TeamToolDispatcher? Dispatcher { get; set; }

        // What this round has made of it.
        public ValidatedVerdict? Verdict { get; set; }

        public int Refusals { get; set; }

        public bool GaveUp { get; set; }

        public string? Unavailable { get; set; }

        public void BeginRound()
        {
            Verdict = null;
            Refusals = 0;
            GaveUp = false;
            Unavailable = null;
        }
    }

    private readonly Dictionary<string, JudgeAgent> _judges = new(StringComparer.Ordinal);

    /// <summary>
    /// Every judge reads the team branch as it is now and gives a verdict - alone, never seeing another's findings. A judge that
    /// only reads works on the shared read-only checkout; one that writes tests has a worktree of its own. Each verdict is checked
    /// (<see cref="VerdictValidator"/>): the verdict word must agree with the findings, a medium or high finding must carry evidence
    /// and a quote that is really in a file it names, and the security judge must answer every misuse case. A finding whose quote
    /// is not there is kept but set aside as unverified. A judge that cannot give a valid verdict is unavailable for the round.
    /// </summary>
    /// <param name="diffFrom">What the judges are shown the changes since: the snapshot for the first round.</param>
    private async Task<JudgeRound> JudgeStageAsync(
        string goal,
        string? approvedPlan,
        RunWorkspace workspace,
        WorkspaceWorktree readOnly,
        GateResult gate,
        string diffFrom,
        int round,
        CancellationToken cancellationToken)
    {
        var design = _design!;
        var threshold = _options.PipelineSeverityThreshold;
        var roster = new List<JudgeAgent>();
        foreach (var standard in PipelineRoles.StandardJudges)
        {
            roster.Add(JudgeFor(standard, []));
        }

        foreach (var custom in _roster.CustomJudges)
        {
            roster.Add(JudgeFor(custom.Agent, custom.FocusFiles));
        }

        SetStage($"Judge - {roster.Count} judges reviewing{(round > 1 ? $" (round {round})" : "")}");
        using var clock = _budget.BeginStage(UltraStage.Judge, round);

        // What every judge reads: the team branch as it stands, on the shared checkout.
        var head = await workspace.TeamHeadAsync(cancellationToken);
        await workspace.MoveDetachedAsync(readOnly, head, cancellationToken);
        var readDirectory = Path.GetFullPath(Path.Combine(readOnly.Path, workspace.SolutionRelativeDirectory));
        var diff = await ReadDiffAsync(workspace, diffFrom, head, cancellationToken);

        foreach (var judge in roster)
        {
            judge.BeginRound();
            if (judge.WritesTests)
            {
                judge.Unavailable = await PrepareJudgeWorktreeAsync(judge, workspace, head, cancellationToken);
            }
        }

        var running = roster.Where(j => j.Unavailable is null).ToList();
        var outcomes = await _workerHost.RunAllAsync(
            [.. running.Select(judge => JudgeAssignment(judge, goal, approvedPlan, design, gate, diff, threshold, readDirectory, round))],
            clock,
            cancellationToken);

        var reports = new List<JudgeReport>();
        foreach (var judge in roster)
        {
            var outcome = running.IndexOf(judge) is var at and >= 0 ? outcomes[at] : null;
            if (judge.Verdict is null)
            {
                judge.Unavailable ??= outcome?.Gap ?? "it gave no verdict";
                Gap($"{judge.Name} ({judge.Agent.Role.DisplayName}) gave no verdict in round {round}: {judge.Unavailable} The round goes on without it.");
            }

            var (branch, commit) = await EvidenceOfAsync(judge, workspace, cancellationToken);
            var report = new JudgeReport(judge.Agent, judge.Verdict, judge.Verdict is null ? judge.Unavailable : null, branch, commit);
            reports.Add(report);
            await RecordAsync(
                $"loop-{round}/verdicts/{judge.Name}.json",
                new VerdictRecord(
                    judge.Name, report.Title, report.Verdict is not null, report.Unavailable, report.Verdict?.Verdict, report.Verdict?.Summary,
                    report.Verdict?.Findings ?? [], report.Verdict?.Coverage ?? [], branch, commit),
                cancellationToken);
        }

        var result = new JudgeRound(round, head, reports);
        var failed = reports.Count(r => r.Verdict is { } v && v.BlocksPass(threshold));
        LogStage(
            clock, UltraStage.Judge, round, [.. outcomes],
            $"{reports.Count} judge(s): {reports.Count(r => r.Verdict is not null) - failed} pass, {failed} fail, {result.Unavailable.Count} unavailable");
        return result;
    }

    private JudgeAgent JudgeFor(PipelineAgent agent, IReadOnlyList<string> focusFiles)
    {
        if (!_judges.TryGetValue(agent.Name, out var judge))
        {
            judge = _judges[agent.Name] = new JudgeAgent(agent, focusFiles) { State = NewAgent(agent.Name) };
        }

        return judge;
    }

    private async Task<string> ReadDiffAsync(RunWorkspace workspace, string from, string to, CancellationToken cancellationToken)
    {
        try
        {
            return await _teamGit.GetDiffTextAsync(workspace.RepositoryRoot, from, to, PipelinePrompts.MaxJudgeDiffChars, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return $"(the diff could not be read: {ex.Message})";
        }
    }

    /// <summary>
    /// A test-writing judge's worktree, made the first time and brought up to the team branch after that. Returns why it could not
    /// be had - there is no room on the disk - or null. That judge is then unavailable for the round; a judge that cannot have a
    /// checkout must never cost the run the work already done, which is what refusing the whole run for the disk would do.
    /// </summary>
    private async Task<string?> PrepareJudgeWorktreeAsync(
        JudgeAgent judge, RunWorkspace workspace, string head, CancellationToken cancellationToken)
    {
        if (judge.Worktree is null)
        {
            try
            {
                workspace.EnsureDiskSpace(1, workspace.WorktreeBytes, $"{judge.Name}'s worktree");
            }
            catch (UltraTeamDiskSpaceException ex)
            {
                return $"{ex.Message}";
            }

            judge.Worktree = await workspace.AddWorktreeAsync(judge.Name, head, cancellationToken);
            judge.Dispatcher = new TeamToolDispatcher(
                workspace.ContextFor(judge.Worktree, _testProjectPaths),
                judge.State!,
                Board,
                new TeamMessageBus(_time, [judge.Name]),
                _readOnlyTools,
                _gitService,
                _teamGit,
                _buildSlots,
                _options.Configuration,
                runCommand: null,
                role: judge.Agent.Role);
            return null;
        }

        var sync = await _teamGit.MergeBranchIntoWorktreeAsync(judge.Worktree.Path, workspace.TeamBranch, judge.Worktree.Author, cancellationToken);
        return sync.Status is TeamSyncStatus.UpToDate or TeamSyncStatus.Merged
            ? null
            : $"the team branch could not be brought into its worktree ({sync.Message}).";
    }

    private PipelineAgentAssignment JudgeAssignment(
        JudgeAgent judge,
        string goal,
        string? approvedPlan,
        DesignPlan design,
        GateResult gate,
        string diff,
        TeamFindingSeverity threshold,
        string readDirectory,
        int round)
    {
        var hasTestProject = _testProjectPaths.Count > 0;

        // A judge that is back for another round is shown its own findings that were still open - its own only - so it can say which
        // were fixed. Which it reports again, under the same title, is how the ledger knows they are the same.
        var earlier = round > 1 ? _ledger.Findings.Where(f => f.Judge == judge.Name && f.IsBlocking(threshold)).ToList() : [];
        var misuse = judge.Name == "j3" ? design.MisuseCases.Select(m => m.Id).ToHashSet(StringComparer.Ordinal) : [];
        var role = judge.Agent.Role;
        return new PipelineAgentAssignment
        {
            Name = judge.Name,
            SystemPrompt = PipelinePrompts.Judge(judge.Name, role, threshold, judge.WritesTests, hasTestProject),
            Opening = PipelinePrompts.JudgeOpening(
                goal, approvedPlan, design, gate.Describe(), diff, judge.FocusFiles, [.. misuse.Order(StringComparer.Ordinal)], round, earlier, _digest),
            Tools = judge.Dispatcher is { } dispatcher
                ? [.. dispatcher.Tools.Where(t => PipelineStageTools.TestWritingJudgeToolNames.Contains(t.Name)), PipelineStageTools.VerdictSubmit]
                : PipelineStageTools.ForReadingJudge,
            SubmitToolName = PipelineStageTools.VerdictSubmitToolName,
            IsComplete = () => judge.Verdict is not null || judge.GaveUp,
            Effort = _options.WorkerEffort ?? role.DefaultEffort,
            MaxIterations = JudgeIterations,
            State = judge.State,
            ExecuteTool = async (tool, arguments, ct) =>
            {
                if (tool == PipelineStageTools.VerdictSubmitToolName)
                {
                    judge.State!.RecordStep(tool);
                    return SubmitVerdict(judge, arguments, threshold, misuse, readDirectory);
                }

                if (judge.Dispatcher is { } worktreeDispatcher)
                {
                    // A judge is given less than its role is offered; what it is not given it does not get.
                    return PipelineStageTools.TestWritingJudgeToolNames.Contains(tool)
                        ? await worktreeDispatcher.ExecuteAsync(tool, arguments, ct)
                        : (true, $"Refused: '{tool}' is not one of your tools. Use the tools in your list.");
                }

                judge.State!.RecordStep(tool);
                return await _readOnlyTools.ExecuteAsync(tool, arguments, readDirectory, ct);
            },
        };
    }

    /// <summary>
    /// A judge's verdict, checked. A verdict word that was left out is refused outright - it would read as the first value, a pass,
    /// and a judge that said nothing must never pass anything. After <see cref="MaxVerdictRefusals"/> refusals the judge is given
    /// up on for the round.
    /// </summary>
    private (bool IsError, string Text) SubmitVerdict(
        JudgeAgent judge, JsonNode? arguments, TeamFindingSeverity threshold, IReadOnlySet<string> misuseIds, string readDirectory)
    {
        var tool = PipelineStageTools.VerdictSubmitToolName;
        if (judge.Verdict is not null)
        {
            return (true, "Your verdict is already recorded. You are done - stop.");
        }

        (bool IsError, string Text) Refuse(string feedback)
        {
            judge.Refusals++;
            if (judge.Refusals >= MaxVerdictRefusals)
            {
                judge.GaveUp = true;
                judge.Unavailable = $"its verdict was refused {judge.Refusals} times and it was not asked again";
                return (true, PipelinePrompts.NotRecorded(tool, feedback) + "\nThat was your last attempt: you are done.");
            }

            return (true, PipelinePrompts.NotRecorded(tool, feedback));
        }

        if (arguments?["verdict"] is null)
        {
            return Refuse("- 'verdict' is required: \"pass\" or \"fail\".");
        }

        var parsed = PipelineToolArguments.Parse<JudgeVerdict>(arguments, tool);
        if (parsed.Value is null)
        {
            return Refuse(parsed.Feedback);
        }

        var context = new VerdictContext(
            threshold,
            path => ReadFile(readDirectory, path),
            misuseIds.Count == 0 ? null : misuseIds,
            RequireBranchEvidenceForHigh: judge.WritesTests);
        var check = VerdictValidator.Validate(parsed.Value, context);
        if (!check.IsValid)
        {
            return Refuse(check.Feedback);
        }

        judge.Verdict = check.Value;
        return (false, "Verdict recorded. You are done.");
    }

    /// <summary>A file's text as the team branch has it, for checking a quote against - the shared checkout is at that commit.</summary>
    private static string? ReadFile(string readDirectory, string path) =>
        WorkspaceFileReader.ResolveWithinRoot(readDirectory, path) is { } full && File.Exists(full) ? File.ReadAllText(full) : null;

    /// <summary>The branch a test-writing judge's failing tests are on, when it committed any that the team branch does not have.</summary>
    private async Task<(string? Branch, string? Commit)> EvidenceOfAsync(
        JudgeAgent judge, RunWorkspace workspace, CancellationToken cancellationToken)
    {
        if (judge.Worktree is null)
        {
            return (null, null);
        }

        try
        {
            var head = await _teamGit.GetHeadAsync(judge.Worktree.Path, cancellationToken);
            return await _teamGit.IsAncestorAsync(workspace.RepositoryRoot, head, workspace.TeamBranch, cancellationToken)
                ? (null, null)
                : (judge.Worktree.Branch, head);
        }
        catch (InvalidOperationException)
        {
            return (null, null);
        }
    }
}
