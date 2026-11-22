using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

// The design stage: one designer, d1, plans the build and writes the interface contract.
public sealed partial class UltraPipelineRun
{
    private const int DesignerIterations = 60;

    /// <summary>What the designer handed in, once the runner has accepted it.</summary>
    private sealed class DesignRun
    {
        public DesignPlan? Accepted { get; set; }

        /// <summary>The commit that carries the interface stubs and was landed on the team branch; null for a
        /// design with no stubs, which has nothing to land.</summary>
        public string? ContractCommit { get; set; }
    }

    /// <summary>
    /// d1 reads the surveys and the code, decides the files and who owns each, writes the interface stubs in
    /// its own checkout and commits them, then submits the design. The runner accepts it only when the design
    /// is sound, the commit changes the declared stubs and nothing else, and the commit builds - and then lands
    /// it on the team branch, so every owner starts from the same contract. Returns false when there is no
    /// accepted design: with nothing planned there is nothing to build, and the run ends.
    /// </summary>
    private async Task<bool> DesignStageAsync(
        string goal, string? approvedPlan, RunWorkspace workspace, WorkspaceWorktree readOnly, CancellationToken cancellationToken)
    {
        SetStage("Design - d1 is planning the build");
        using var clock = _budget.BeginStage(UltraStage.Design);
        var agent = PipelineRoles.DesignerAgent;
        var limits = new DesignLimits(MaxOwners: Math.Max(1, _options.PipelineMaxOwners));

        var worktree = await workspace.AddWorktreeAsync(agent.Name, workspace.Start, cancellationToken);
        var state = NewAgent(agent.Name);
        var dispatcher = new TeamToolDispatcher(
            workspace.ContextFor(worktree, _testProjectPaths),
            state,
            Board,
            new TeamMessageBus(_time, [agent.Name]),
            _readOnlyTools,
            _gitService,
            _teamGit,
            _buildSlots,
            _options.Configuration,
            runCommand: null,
            role: agent.Role);

        var design = new DesignRun();
        var outcome = await _workerHost.RunAsync(
            new PipelineAgentAssignment
            {
                Name = agent.Name,
                SystemPrompt = PipelinePrompts.Designer(agent.Name, agent.Role, limits),
                Opening = PipelinePrompts.DesignerOpening(goal, approvedPlan, _surveys, _missingSurveys),
                Tools = [.. dispatcher.Tools.Where(t => PipelineStageTools.DesignerToolNames.Contains(t.Name)), PipelineStageTools.DesignSubmit],
                SubmitToolName = PipelineStageTools.DesignSubmitToolName,
                IsComplete = () => design.Accepted is not null,
                Effort = _options.WorkerEffort ?? agent.Role.DefaultEffort,
                MaxIterations = DesignerIterations,
                State = state,
                ExecuteTool = async (tool, arguments, ct) =>
                {
                    if (tool == PipelineStageTools.DesignSubmitToolName)
                    {
                        state.RecordStep(tool);
                        return await SubmitDesignAsync(design, arguments, limits, workspace, worktree, dispatcher, ct);
                    }

                    // The dispatcher offers the role more than the designer is given, and refuses nothing the
                    // role is offered; what the designer is not given it does not get.
                    return PipelineStageTools.DesignerToolNames.Contains(tool)
                        ? await dispatcher.ExecuteAsync(tool, arguments, ct)
                        : (true, $"Refused: '{tool}' is not one of your tools. Read, edit, build, team_commit and {PipelineStageTools.DesignSubmitToolName} are.");
                },
            },
            clock,
            cancellationToken);

        if (outcome.Gap is not null)
        {
            Gap(outcome.Gap);
        }

        if (design.Accepted is not { } plan)
        {
            Gap("d1 handed in no design, so there is nothing to build.");
            LogStage(clock, UltraStage.Design, 0, [outcome], "no design was accepted");
            return false;
        }

        _design = plan;
        _ownership = OwnershipMap.From(plan);
        await RecordAsync("ownership.json", OwnershipRecord.From(_ownership), cancellationToken);
        await RecordAsync("design.json", new DesignRecord(plan, design.ContractCommit), cancellationToken);

        // What the readers see from here on includes the contract.
        if (design.ContractCommit is { } contract)
        {
            await workspace.MoveDetachedAsync(readOnly, contract, cancellationToken);
        }

        var stubs = plan.Files.Count(f => f.IsStub);
        LogStage(
            clock, UltraStage.Design, 0, [outcome],
            design.ContractCommit is { } commit
                ? $"contract {commit[..Math.Min(8, commit.Length)]} landed: {stubs} stub file(s), {plan.Owners.Count} owner(s)"
                : $"no stubs, so no contract commit; {plan.Owners.Count} owner(s)");
        return true;
    }

    /// <summary>
    /// What happens when the designer calls design_submit: the design must be sound, the contract commit must
    /// change the stubs it declares and nothing else and must build, and then it is landed. The first thing
    /// wrong is the answer, with what to do about it; nothing is landed until nothing is wrong.
    /// </summary>
    private async Task<(bool IsError, string Text)> SubmitDesignAsync(
        DesignRun design,
        JsonNode? arguments,
        DesignLimits limits,
        RunWorkspace workspace,
        WorkspaceWorktree worktree,
        TeamToolDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        var tool = PipelineStageTools.DesignSubmitToolName;
        if (design.Accepted is not null)
        {
            return (true, "The design is already recorded. You are done - stop.");
        }

        var parsed = PipelineToolArguments.Parse<DesignPlan>(arguments, tool);
        if (parsed.Value is null)
        {
            return (true, PipelinePrompts.NotRecorded(tool, parsed.Feedback));
        }

        if (parsed.Value.Waves > 1)
        {
            return (true, PipelinePrompts.NotRecorded(
                tool, "- This run builds in one wave: put every interface stub in your one contract commit, and leave 'waves' out."));
        }

        var checkedDesign = DesignValidator.Validate(parsed.Value, limits);
        if (!checkedDesign.IsValid)
        {
            return (true, PipelinePrompts.NotRecorded(tool, checkedDesign.Feedback));
        }

        var plan = checkedDesign.Value!;
        if (await _teamGit.HasUncommittedChangesAsync(worktree.Path, cancellationToken))
        {
            return (true, PipelinePrompts.NotRecorded(
                tool, "- Your checkout has uncommitted changes. team_commit your contract (or undo what is not part of it), then call this again."));
        }

        var head = await _teamGit.GetHeadAsync(worktree.Path, cancellationToken);
        var changes = await _teamGit.GetChangedFilesAsync(workspace.RepositoryRoot, workspace.Start, head, cancellationToken);
        var inSolution = new List<string>();
        var outside = new List<string>();
        foreach (var change in changes)
        {
            if (workspace.ToSolutionPath(change.RelativePath) is { } relative)
            {
                inSolution.Add(relative);
            }
            else
            {
                outside.Add(change.RelativePath);
            }
        }

        var contract = ContractValidator.Validate(plan, inSolution, outside);
        if (!contract.IsValid)
        {
            return (true, PipelinePrompts.NotRecorded(tool, contract.Feedback));
        }

        if (changes.Count > 0)
        {
            // Every owner builds against this commit, so it has to build before anyone does.
            var (built, report) = await dispatcher.PreflightBuildAsync(cancellationToken);
            if (!built)
            {
                return (true, PipelinePrompts.NotRecorded(
                    tool, "- Your contract commit does not build, and it must: every owner builds against it. Fix it, team_commit, and call this again.\n\n" + report));
            }

            try
            {
                await workspace.LandAsync(head, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                return (true, PipelinePrompts.NotRecorded(tool, $"- Your contract commit could not be put on the team branch: {ex.Message}"));
            }

            design.ContractCommit = head;
        }

        design.Accepted = plan;
        return (false, changes.Count > 0
            ? "Design recorded, and your contract commit is on the team branch. You are done."
            : "Design recorded. You are done.");
    }
}
