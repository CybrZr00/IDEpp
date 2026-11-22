using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

// The build stage: the design's owners build their files, in parallel, each in its own worktree.
public sealed partial class UltraPipelineRun
{
    private const int OwnerIterations = 120;

    /// <summary>
    /// One owner and everything the later stages need to carry on with it: its worktree and dispatcher (so the
    /// agent that wrote a file is the one that repairs it), its files, and what it reported.
    /// </summary>
    private sealed class OwnerAgent(
        PipelineAgent agent, IReadOnlyList<string> files, WorkspaceWorktree worktree, TeamToolDispatcher dispatcher, TeamWorkerState state)
    {
        public PipelineAgent Agent { get; } = agent;

        public string Name => Agent.Name;

        public IReadOnlyList<string> Files { get; } = files;

        public WorkspaceWorktree Worktree { get; } = worktree;

        public TeamToolDispatcher Dispatcher { get; } = dispatcher;

        public TeamWorkerState State { get; } = state;

        /// <summary>The owner has reported. Set once, by <see cref="SubmitBuildAsync"/>.</summary>
        public bool Submitted { get; set; }

        public BuildSubmission? Report { get; set; }

        /// <summary>It has been told once that it has work that is not merged.</summary>
        public bool WarnedAboutUnmerged { get; set; }

        /// <summary>What it left unmerged when it reported anyway ("uncommitted changes"), or null.</summary>
        public string? LeftUnmerged { get; set; }
    }

    /// <summary>The owners, in order. They outlive the build: the repair stage hands each its findings.</summary>
    private readonly List<OwnerAgent> _owners = [];

    /// <summary>
    /// Every owner starts at once, from the contract commit, each in a worktree of its own, and builds the files the
    /// design gave it. What an owner may edit is its own files and nothing else - its dispatcher enforces that as it
    /// edits and again when it merges - and its work counts only once it is on the team branch, which every merge
    /// reaches by building first. The stage ends when every owner has reported or run out of what it was given;
    /// an owner that did not report is a gap in the record and its unmerged work stays on its branch.
    /// </summary>
    private async Task BuildStageAsync(
        string goal, string? approvedPlan, RunWorkspace workspace, WorkspaceWorktree readOnly, CancellationToken cancellationToken)
    {
        var design = _design!;
        SetStage($"Build - {design.Owners.Count} owner{(design.Owners.Count == 1 ? "" : "s")} building their files");
        using var clock = _budget.BeginStage(UltraStage.Build);

        // The owners are known now, so their worktrees can be counted: refused here, before any is made, if
        // the disk will not take them.
        workspace.EnsureDiskSpace(design.Owners.Count, workspace.WorktreeBytes, "the owners' worktrees");

        var teamHead = await workspace.TeamHeadAsync(cancellationToken);
        var hooks = new TeamPipelineHooks(() => _ownership);
        var bus = new TeamMessageBus(_time, design.Owners);
        for (var i = 0; i < design.Owners.Count; i++)
        {
            var agent = PipelineRoles.OwnerAgent(i + 1);
            var worktree = await workspace.AddWorktreeAsync(agent.Name, teamHead, cancellationToken);
            var state = NewAgent(agent.Name);
            var dispatcher = new TeamToolDispatcher(
                workspace.ContextFor(worktree, _testProjectPaths),
                state,
                Board,
                bus,
                _readOnlyTools,
                _gitService,
                _teamGit,
                _buildSlots,
                _options.Configuration,
                _runCommand,
                role: agent.Role,
                pipeline: hooks);
            _owners.Add(new OwnerAgent(agent, [.. design.FilesOf(agent.Name).Select(f => f.Path)], worktree, dispatcher, state));
        }

        var outcomes = await _workerHost.RunAllAsync(
            [.. _owners.Select(owner => OwnerAssignment(goal, approvedPlan, design, workspace, owner))], clock, cancellationToken);

        for (var i = 0; i < _owners.Count; i++)
        {
            var owner = _owners[i];
            var outcome = outcomes[i];
            if (outcome.Gap is not null)
            {
                Gap(outcome.Gap);
            }

            if (owner.LeftUnmerged is { } unmerged)
            {
                Gap($"{owner.Name} reported with {unmerged}: that work is on its branch, not the team branch.");
            }

            await RecordAsync(
                $"build/{owner.Name}.json",
                new BuildRecord(
                    owner.Name, owner.Files, owner.Submitted, owner.Report?.Summary, owner.Report?.Unmet ?? [],
                    owner.State.Merges, owner.State.Steps, outcome.Gap),
                cancellationToken);
        }

        // The stage is over: what the readers see is the team branch as the owners left it.
        var head = await workspace.TeamHeadAsync(cancellationToken);
        if (head != teamHead)
        {
            await workspace.MoveDetachedAsync(readOnly, head, cancellationToken);
        }

        var merges = _owners.Sum(o => o.State.Merges);
        LogStage(clock, UltraStage.Build, 0, outcomes, $"{merges} merge(s) from {_owners.Count} owner(s)");
    }

    private PipelineAgentAssignment OwnerAssignment(
        string goal, string? approvedPlan, DesignPlan design, RunWorkspace workspace, OwnerAgent owner) => new()
    {
        Name = owner.Name,
        SystemPrompt = PipelinePrompts.Owner(owner.Name, owner.Agent.Role, owner.Files),
        Opening = PipelinePrompts.OwnerOpening(goal, approvedPlan, design, owner.Name, owner.Files),
        Tools = [.. owner.Dispatcher.Tools.Where(t => PipelineStageTools.OwnerToolNames.Contains(t.Name)), PipelineStageTools.BuildSubmit],
        SubmitToolName = PipelineStageTools.BuildSubmitToolName,
        IsComplete = () => owner.Submitted,
        Effort = _options.WorkerEffort ?? owner.Agent.Role.DefaultEffort,
        MaxIterations = OwnerIterations,
        State = owner.State,
        ExecuteTool = async (tool, arguments, ct) =>
        {
            if (tool == PipelineStageTools.BuildSubmitToolName)
            {
                owner.State.RecordStep(tool);
                return await SubmitBuildAsync(owner, arguments, workspace, ct);
            }

            // The dispatcher offers the role more than an owner is given; what it is not given it does not get.
            return PipelineStageTools.OwnerToolNames.Contains(tool)
                ? await owner.Dispatcher.ExecuteAsync(tool, arguments, ct)
                : (true, $"Refused: '{tool}' is not one of your tools. Use the tools in your list.");
        },
    };

    /// <summary>
    /// An owner's report that it is done. Unmerged work is the one thing worth stopping it for: it is lost when the
    /// run ends, and an owner that has just finished is the one most likely to have forgotten it. It is told once;
    /// the second time the report is taken - the owner may have a reason it cannot merge, and it has said so - and
    /// the unmerged work is a gap in the record.
    /// </summary>
    private async Task<(bool IsError, string Text)> SubmitBuildAsync(
        OwnerAgent owner, JsonNode? arguments, RunWorkspace workspace, CancellationToken cancellationToken)
    {
        var tool = PipelineStageTools.BuildSubmitToolName;
        if (owner.Submitted)
        {
            return (true, "Your report is already recorded. You are done - stop.");
        }

        var parsed = PipelineToolArguments.Parse<BuildSubmission>(arguments, tool);
        if (parsed.Value is null)
        {
            return (true, PipelinePrompts.NotRecorded(tool, parsed.Feedback));
        }

        var report = BuildSubmissionValidator.Validate(parsed.Value);
        if (!report.IsValid)
        {
            return (true, PipelinePrompts.NotRecorded(tool, report.Feedback));
        }

        var unmerged = await DescribeUnmergedWorkAsync(owner, workspace, cancellationToken);
        if (unmerged is not null && !owner.WarnedAboutUnmerged)
        {
            owner.WarnedAboutUnmerged = true;
            return (true, PipelinePrompts.NotRecorded(
                tool,
                $"- You still have {unmerged}, and anything unmerged is lost when the run ends. Merge it first: team_commit, "
                + "team_sync if the team branch has moved on, build, team_merge. If it cannot be merged, say why under 'unmet' "
                + "and call this again."));
        }

        owner.Report = report.Value;
        owner.LeftUnmerged = unmerged;
        owner.Submitted = true;
        return (false, "Report recorded. You are done.");
    }

    /// <summary>What an owner has that the team branch does not, or null when everything it did is merged.</summary>
    private async Task<string?> DescribeUnmergedWorkAsync(OwnerAgent owner, RunWorkspace workspace, CancellationToken cancellationToken)
    {
        if (await _teamGit.HasUncommittedChangesAsync(owner.Worktree.Path, cancellationToken))
        {
            return "uncommitted changes";
        }

        var head = await _teamGit.GetHeadAsync(owner.Worktree.Path, cancellationToken);
        return await _teamGit.IsAncestorAsync(workspace.RepositoryRoot, head, workspace.TeamBranch, cancellationToken)
            ? null
            : "commits that are not on the team branch";
    }
}
