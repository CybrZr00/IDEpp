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
        private readonly HashSet<string> _told = new(StringComparer.Ordinal);

        public PipelineAgent Agent { get; } = agent;

        public string Name => Agent.Name;

        public IReadOnlyList<string> Files { get; } = files;

        public WorkspaceWorktree Worktree { get; } = worktree;

        public TeamToolDispatcher Dispatcher { get; } = dispatcher;

        public TeamWorkerState State { get; } = state;

        /// <summary>The owner has reported. Set by <see cref="SubmitBuildAsync"/>, and cleared when the owner is called
        /// back, so that it ends by reporting again.</summary>
        public bool Submitted { get; set; }

        public BuildSubmission? Report { get; set; }

        /// <summary>It has been told once that it has work that is not merged.</summary>
        public bool WarnedAboutUnmerged { get; set; }

        /// <summary>What it left unmerged when it reported anyway ("uncommitted changes"), or null.</summary>
        public string? LeftUnmerged { get; set; }

        /// <summary>An agent of its own is running now - its first conversation, or one it was called back for. Guarded by
        /// <c>_wakeLock</c>, with <see cref="WakeScheduled"/> and <see cref="Wakes"/>.</summary>
        public bool IsRunning { get; set; }

        public bool WakeScheduled { get; set; }

        /// <summary>How many times it has been called back to answer or carry out a request since the counter was last reset.</summary>
        public int Wakes { get; set; }

        /// <summary>A new stage begins: the owner has not reported in it, has no unmerged work to answer for, and may be called back
        /// again as often as a stage allows.</summary>
        public void BeginStage()
        {
            Submitted = false;
            Report = null;
            WarnedAboutUnmerged = false;
            LeftUnmerged = null;
            IsRunning = false;
            WakeScheduled = false;
            Wakes = 0;
        }

        /// <summary>True the first time <paramref name="what"/> is said to this owner, false after - so a request is told once
        /// and not at the end of every tool call.</summary>
        public bool Tell(string what)
        {
            lock (_told)
            {
                return _told.Add(what);
            }
        }
    }

    /// <summary>The owners, in order. They outlive the build: the repair stage hands each its findings.</summary>
    private readonly List<OwnerAgent> _owners = [];

    /// <summary>
    /// Every owner starts at once, from the contract commit, each in a worktree of its own, and builds the files the
    /// design gave it. What an owner may edit is its own files and nothing else - its dispatcher enforces that as it
    /// edits and again when it merges - and its work counts only once it is on the team branch, which every merge
    /// reaches by building first. An owner whose work needs another owner's file asks for it
    /// (<see cref="PipelineChangeTools"/>); an owner that has finished is called back to answer. The stage ends when
    /// every owner has reported or run out of what it was given, and nobody is waiting on anybody; an owner that did
    /// not report is a gap in the record and its unmerged work stays on its branch.
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
        var bus = new TeamMessageBus(_time, design.Owners);
        for (var i = 0; i < design.Owners.Count; i++)
        {
            var agent = PipelineRoles.OwnerAgent(i + 1);
            var name = agent.Name;
            var worktree = await workspace.AddWorktreeAsync(name, teamHead, cancellationToken);
            var state = NewAgent(name);

            // Each owner's own rules: only its files, and a colleague's request is answered before anything else.
            var hooks = new TeamPipelineHooks(() => _ownership, tool => ChangeRequestGuard(name, tool));
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
            _owners.Add(new OwnerAgent(agent, [.. design.FilesOf(name).Select(f => f.Path)], worktree, dispatcher, state));
        }

        var work = new WorkContext(clock, workspace, goal, approvedPlan, design, cancellationToken);
        List<PipelineAgentOutcome> outcomes;
        IReadOnlyList<PipelineAgentOutcome> calledBack;
        foreach (var owner in _owners)
        {
            owner.IsRunning = true; // before any of them starts, so a request to one that has not yet begun is not taken for one that is idle
        }

        lock (_wakeLock)
        {
            _serving = work;
        }

        try
        {
            outcomes = [.. await Task.WhenAll(_owners.Select(owner => RunOwnerAsync(owner, work)))];
            calledBack = await DrainWakesAsync(work);
        }
        finally
        {
            lock (_wakeLock)
            {
                _serving = null;
            }
        }

        // Whatever is still open is never going to be answered: say so to whoever might be waiting.
        foreach (var agent in _changes.All.Where(r => r.IsOpen).SelectMany(r => new[] { r.From, r.To }).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            _changes.CancelAllFor(agent, "The build stage ended before it was settled.");
        }

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

        await RecordAsync("build/change-requests.json", new ChangeRequestsRecord(_changes.All), cancellationToken);

        // The stage is over: what the readers see is the team branch as the owners left it.
        var head = await workspace.TeamHeadAsync(cancellationToken);
        if (head != teamHead)
        {
            await workspace.MoveDetachedAsync(readOnly, head, cancellationToken);
        }

        var merges = _owners.Sum(o => o.State.Merges);
        var requests = _changes.All.Count;
        LogStage(
            clock, UltraStage.Build, 0, [.. outcomes, .. calledBack],
            $"{merges} merge(s) from {_owners.Count} owner(s)" + (requests > 0 ? $", {requests} change request(s)" : ""));
    }

    /// <summary>
    /// One owner's own conversation: it builds, and ends by reporting. When it ends - whichever way - requests that
    /// came for it while it was busy get their answer: it is called back, or they are cancelled. One that did not report
    /// takes everything that involved it down with it: nobody is left to answer or to read the answer.
    /// </summary>
    private async Task<PipelineAgentOutcome> RunOwnerAsync(OwnerAgent owner, WorkContext work)
    {
        PipelineAgentOutcome outcome;
        try
        {
            outcome = await _workerHost.RunAsync(OwnerAssignment(owner, work, calledBackFor: null), work.Clock, work.Token);
        }
        finally
        {
            lock (_wakeLock)
            {
                owner.IsRunning = false;
            }
        }

        if (!outcome.Submitted)
        {
            _changes.CancelAllFor(owner.Name, $"{owner.Name} stopped without finishing.");
        }

        EnsureServed(owner.Name);
        return outcome;
    }

    private PipelineAgentAssignment OwnerAssignment(OwnerAgent owner, WorkContext work, IReadOnlyList<ChangeRequest>? calledBackFor) => new()
    {
        Name = owner.Name,
        SystemPrompt = work.Repairs is null
            ? PipelinePrompts.Owner(owner.Name, owner.Agent.Role, owner.Files)
            : PipelinePrompts.OwnerRepair(owner.Name, owner.Agent.Role, owner.Files),
        Opening = calledBackFor is not null
            ? PipelinePrompts.OwnerWakeOpening(work.Goal, work.ApprovedPlan, work.Design, owner.Name, owner.Files, calledBackFor)
            : work.RepairsFor(owner.Name) is { } repair
                ? PipelinePrompts.OwnerRepairOpening(work.Goal, work.ApprovedPlan, work.Design, owner.Name, owner.Files, repair, work.Round, _digest)
                : PipelinePrompts.OwnerOpening(work.Goal, work.ApprovedPlan, work.Design, owner.Name, owner.Files),
        Tools =
        [
            .. owner.Dispatcher.Tools.Where(t => PipelineStageTools.OwnerToolNames.Contains(t.Name)),
            PipelineStageTools.BuildSubmit,
            .. PipelineChangeTools.All,
        ],
        SubmitToolName = PipelineStageTools.BuildSubmitToolName,
        IsComplete = () => owner.Submitted,
        Effort = _options.WorkerEffort ?? owner.Agent.Role.DefaultEffort,
        MaxIterations = calledBackFor is not null ? CalledBackIterations : work.Repairs is null ? OwnerIterations : RepairIterations,
        State = owner.State,
        DrainNotices = () => OwnerNotices(owner),
        ExecuteTool = async (tool, arguments, ct) =>
        {
            if (tool == PipelineStageTools.BuildSubmitToolName)
            {
                owner.State.RecordStep(tool);
                return await SubmitBuildAsync(owner, arguments, work.Workspace, ct);
            }

            if (PipelineChangeTools.IsChangeTool(tool))
            {
                owner.State.RecordStep(tool);
                return await ExecuteChangeToolAsync(owner, work, tool, arguments, ct);
            }

            // The dispatcher offers the role more than an owner is given; what it is not given it does not get.
            return PipelineStageTools.OwnerToolNames.Contains(tool)
                ? await owner.Dispatcher.ExecuteAsync(tool, arguments, ct)
                : (true, $"Refused: '{tool}' is not one of your tools. Use the tools in your list.");
        },
    };

    /// <summary>
    /// An owner's report that it is done. A request that involves it comes first: one waiting for its answer would be
    /// left unanswered, and one it made and has not seen through would be left with nobody to read the answer. Unmerged
    /// work is the other thing worth stopping it for: it is lost when the run ends, and an owner that has just finished
    /// is the one most likely to have forgotten it. That it is told once; the second time the report is taken - the owner
    /// may have a reason it cannot merge, and it has said so - and the unmerged work is a gap in the record.
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

        _changes.Sweep();
        if (ChangeRequestMessages.BlocksReport(_changes.OpenFor(owner.Name), owner.Name) is { } involved)
        {
            return (true, PipelinePrompts.NotRecorded(tool, $"- {involved}"));
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
