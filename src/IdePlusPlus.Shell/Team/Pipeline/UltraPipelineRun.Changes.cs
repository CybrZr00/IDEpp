using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;

namespace IdePlusPlus.Shell.Team.Pipeline;

// Change requests between owners: asking another owner for a change to its file, answering, being called back, appealing.
public sealed partial class UltraPipelineRun
{
    /// <summary>How many times an owner is called back in one stage. A request to one that has used them all is
    /// cancelled, so a run in which two owners keep needing each other ends instead of going on.</summary>
    private const int MaxWakesPerOwner = 2;

    private const int CalledBackIterations = 40;

    /// <summary>What the agents of one stage work in: its clock, the workspace, and what every prompt needs. The change
    /// requests' machinery - calling an owner back, deciding an appeal - runs inside whichever stage is current.</summary>
    private sealed class WorkContext(
        StageClock clock, RunWorkspace workspace, string goal, string? approvedPlan, DesignPlan design, CancellationToken token)
    {
        public StageClock Clock { get; } = clock;

        public RunWorkspace Workspace { get; } = workspace;

        public string Goal { get; } = goal;

        public string? ApprovedPlan { get; } = approvedPlan;

        public DesignPlan Design { get; } = design;

        public CancellationToken Token { get; } = token;

        /// <summary>The conversations started to call an owner back. Only ever added to, under <c>_wakeLock</c>.</summary>
        public List<Task<PipelineAgentOutcome>> CalledBack { get; } = [];
    }

    /// <summary>Guards who is running, who is being called back, and the stage's list of callbacks: the decision to call an
    /// owner back is made once, whoever notices it first.</summary>
    private readonly object _wakeLock = new();

    private WorkContext? _serving;

    /// <summary>Held while the ownership map is read, changed and replaced, so two claims for one path cannot both succeed.</summary>
    private readonly object _ownershipLock = new();

    // ---- Calling an owner back ----

    private void OnChangeRequest(ChangeRequest request)
    {
        OnChanged();

        // A request that needs an answer, or - after the lead's ruling - needs doing.
        if (request.State == ChangeRequestState.Pending || (request.State == ChangeRequestState.Accepted && request.Binding))
        {
            EnsureServed(request.To);
        }
    }

    /// <summary>What is waiting for <paramref name="owner"/> to do: requests to answer, and ones it must carry out.</summary>
    private List<ChangeRequest> WaitingOn(string owner) =>
    [
        .. _changes.PendingFor(owner),
        .. _changes.OpenFor(owner).Where(r => r.State == ChangeRequestState.Accepted && SameAgent(r.To, owner)),
    ];

    /// <summary>
    /// If something waits for an owner that is not running, calls it back - a new short conversation, since what it knows
    /// of its own work is in its checkout - unless it has been called back as often as it may be or the stage is over, in
    /// which case the requests that involve it are cancelled and whoever waits on them is told. Called whenever a request
    /// might need it and whenever an owner stops running, so a request that arrives just as an owner finishes is not lost.
    /// </summary>
    private void EnsureServed(string name)
    {
        string? cancelBecause;
        lock (_wakeLock)
        {
            if (_serving is not { } work || _owners.FirstOrDefault(o => SameAgent(o.Name, name)) is not { } owner
                || owner.IsRunning || owner.WakeScheduled)
            {
                return;
            }

            if (WaitingOn(name).Count == 0)
            {
                return;
            }

            if (owner.Wakes >= MaxWakesPerOwner)
            {
                cancelBecause = $"{name} had already been called back {MaxWakesPerOwner} times in this stage, and was not called again.";
            }
            else if (work.Clock.IsOver)
            {
                cancelBecause = $"the stage ran out of time before {name} could be called back.";
            }
            else
            {
                owner.Wakes++;
                owner.WakeScheduled = true;
                owner.IsRunning = true;
                work.CalledBack.Add(Task.Run(() => CallBackAsync(owner, work)));
                return;
            }
        }

        _changes.CancelAllFor(name, cancelBecause);
    }

    private async Task<PipelineAgentOutcome> CallBackAsync(OwnerAgent owner, WorkContext work)
    {
        var waiting = WaitingOn(owner.Name);
        foreach (var request in waiting)
        {
            owner.Tell(request.Binding ? $"{request.Id}:binding" : $"{request.Id}:pending"); // its opening has them
        }

        var hadReported = owner.Submitted;
        owner.Submitted = false; // it ends by reporting again
        PipelineAgentOutcome outcome;
        try
        {
            outcome = await _workerHost.RunAsync(OwnerAssignment(owner, work, calledBackFor: waiting), work.Clock, work.Token);
        }
        finally
        {
            lock (_wakeLock)
            {
                owner.IsRunning = false;
                owner.WakeScheduled = false;
            }
        }

        if (!outcome.Submitted)
        {
            owner.Submitted = hadReported; // what it had reported stands
            if (outcome.Gap is not null)
            {
                Gap(outcome.Gap);
            }

            _changes.CancelAllFor(owner.Name, $"{owner.Name} stopped before it could finish.");
        }

        EnsureServed(owner.Name);
        return outcome;
    }

    /// <summary>
    /// Waits for every callback of the stage, including the ones those start: a callback adds its successor to the list
    /// before it finishes, so the list is complete when the last one it holds is.
    /// </summary>
    private async Task<IReadOnlyList<PipelineAgentOutcome>> DrainWakesAsync(WorkContext work)
    {
        var finished = new List<PipelineAgentOutcome>();
        var awaited = 0;
        while (true)
        {
            Task<PipelineAgentOutcome>[] batch;
            lock (_wakeLock)
            {
                batch = [.. work.CalledBack.Skip(awaited)];
            }

            if (batch.Length == 0)
            {
                return finished;
            }

            finished.AddRange(await Task.WhenAll(batch));
            awaited += batch.Length;
        }
    }

    // ---- What an owner is told, and what it may not do meanwhile ----

    /// <summary>The refusal for editing, committing or merging while a colleague's request waits for this owner's answer.</summary>
    private string? ChangeRequestGuard(string owner, string tool)
    {
        var pending = _changes.PendingFor(owner);
        return pending.Count == 0 ? null : ChangeRequestMessages.GuardRefusal(tool, pending);
    }

    /// <summary>
    /// Appended to an owner's tool results: a request that has come for it, a ruling that it must carry one out, and
    /// where its own requests have got to. Each is said once. An owner that is waiting in await_change is told there.
    /// </summary>
    private string? OwnerNotices(OwnerAgent owner)
    {
        List<string>? lines = null;
        foreach (var request in _changes.All)
        {
            string? notice = null;
            if (SameAgent(request.To, owner.Name))
            {
                if (request.State == ChangeRequestState.Pending && owner.Tell($"{request.Id}:pending"))
                {
                    notice = ChangeRequestMessages.IncomingNotice(request);
                }
                else if (request.State == ChangeRequestState.Accepted && request.Binding && owner.Tell($"{request.Id}:binding"))
                {
                    notice = ChangeRequestMessages.BindingNotice(request);
                }
            }
            else if (SameAgent(request.From, owner.Name)
                     && request.State is ChangeRequestState.Accepted or ChangeRequestState.Rejected or ChangeRequestState.Done
                         or ChangeRequestState.TimedOut or ChangeRequestState.Cancelled
                     && owner.Tell($"{request.Id}:{request.State}"))
            {
                notice = ChangeRequestMessages.Outcome(request);
            }

            if (notice is not null)
            {
                (lines ??= []).Add(notice);
            }
        }

        return lines is null ? null : string.Join("\n\n", lines);
    }

    // ---- The tools ----

    private async Task<(bool IsError, string Text)> ExecuteChangeToolAsync(
        OwnerAgent owner, WorkContext work, string tool, JsonNode? arguments, CancellationToken cancellationToken)
    {
        switch (tool)
        {
            case PipelineChangeTools.RequestChangeToolName:
                return Parse<RequestChangeArguments>(arguments, tool, out var request) is { } requestError
                    ? requestError
                    : RequestChange(owner, request!);

            case PipelineChangeTools.AwaitChangeToolName:
                return Parse<AwaitChangeArguments>(arguments, tool, out var wait) is { } waitError
                    ? waitError
                    : await AwaitChangeAsync(owner, wait!, cancellationToken);

            case PipelineChangeTools.RespondChangeToolName:
                return Parse<RespondChangeArguments>(arguments, tool, out var respond) is { } respondError
                    ? respondError
                    : RespondChange(owner, respond!);

            case PipelineChangeTools.ChangeDoneToolName:
                return Parse<ChangeDoneArguments>(arguments, tool, out var done) is { } doneError
                    ? doneError
                    : await ChangeDoneAsync(owner, work, done!, cancellationToken);

            case PipelineChangeTools.AppealChangeToolName:
                return Parse<AppealChangeArguments>(arguments, tool, out var appeal) is { } appealError
                    ? appealError
                    : await AppealChangeAsync(owner, work, appeal!, cancellationToken);

            case PipelineChangeTools.ClaimNewFileToolName:
                return Parse<ClaimNewFileArguments>(arguments, tool, out var claim) is { } claimError
                    ? claimError
                    : await ClaimNewFileAsync(owner, work, claim!, cancellationToken);

            default:
                return (true, $"'{tool}' is not a change tool.");
        }
    }

    /// <summary>The arguments as <typeparamref name="T"/>, or the refusal to give back when they cannot be read.</summary>
    private static (bool IsError, string Text)? Parse<T>(JsonNode? arguments, string tool, out T? value) where T : class
    {
        var parsed = PipelineToolArguments.Parse<T>(arguments, tool);
        value = parsed.Value;
        return parsed.IsValid ? null : (true, parsed.Feedback);
    }

    private (bool IsError, string Text) RequestChange(OwnerAgent owner, RequestChangeArguments arguments)
    {
        if (PipelineArtifactStore.NormalizeRelativePath(arguments.File) is not { } file)
        {
            return (true, $"'{arguments.File?.Trim()}' is not a plain path relative to the solution folder. Name the file as the design does.");
        }

        if (_ownership.OwnerOf(file) is not { } to)
        {
            return (true, $"{file} is not owned by anyone, so there is nobody to ask. If it does not exist yet and your work needs it, "
                          + "claim_new_file; if it does exist, say in your report that your work needs it changed.");
        }

        var created = _changes.Create(owner.Name, to, file, arguments.Change ?? "", arguments.Rationale ?? "");
        return created.Ok
            ? (false, $"Opened {created.Request!.Id} to {to} for {file}. It returns at once: carry on with your own files, and call "
                      + $"await_change {created.Request.Id} when you need the answer.")
            : (true, created.Error!);
    }

    private async Task<(bool IsError, string Text)> AwaitChangeAsync(
        OwnerAgent owner, AwaitChangeArguments arguments, CancellationToken cancellationToken)
    {
        var id = arguments.Id?.Trim() ?? "";
        var minutes = Math.Clamp(arguments.Minutes ?? PipelineChangeTools.DefaultAwaitMinutes, 1, PipelineChangeTools.MaxAwaitMinutes);
        var request = _changes.Find(id);

        // A wait takes no steps, so the card and the stall checks are told what the owner is doing.
        using var blocked = owner.State.BlockOn(request is null ? id : $"{request.Id} (answer from {request.To})");
        OnChanged();
        var wait = await _changes.WaitForChangeAsync(
            id, owner.Name, TimeSpan.FromTicks(_options.PipelineChangeWaitUnit.Ticks * minutes), cancellationToken);
        OnChanged();

        if (wait.Request is { } settled)
        {
            owner.Tell($"{settled.Id}:{settled.State}"); // it has just been told
        }

        return ChangeRequestMessages.AwaitResult(wait);
    }

    private (bool IsError, string Text) RespondChange(OwnerAgent owner, RespondChangeArguments arguments)
    {
        var decision = arguments.Decision?.Trim().ToLowerInvariant() ?? "";
        bool accept;
        if (decision.StartsWith("accept", StringComparison.Ordinal))
        {
            accept = true;
        }
        else if (decision.StartsWith("reject", StringComparison.Ordinal))
        {
            accept = false;
        }
        else
        {
            return (true, $"decision '{decision}' is not \"accept\" or \"reject\".");
        }

        var id = arguments.Id?.Trim() ?? "";
        var answered = _changes.Respond(id, owner.Name, accept, arguments.Reasoning ?? "");
        if (!answered.Ok)
        {
            return (true, answered.Error!);
        }

        owner.Tell($"{answered.Request!.Id}:pending");
        return accept
            ? (false, $"Accepted {id}. Make the change in your own file, build, team_commit, team_merge, then call change_done {id}.")
            : (false, $"Rejected {id}. {answered.Request.From} may appeal to the lead; if the lead rules that you must make it, you will be told.");
    }

    private async Task<(bool IsError, string Text)> ChangeDoneAsync(
        OwnerAgent owner, WorkContext work, ChangeDoneArguments arguments, CancellationToken cancellationToken)
    {
        var id = arguments.Id?.Trim() ?? "";
        if (await DescribeUnmergedWorkAsync(owner, work.Workspace, cancellationToken) is { } unmerged)
        {
            return (true, $"Not recorded: you still have {unmerged}. The change has to be on the team branch before you report it done: "
                          + $"team_commit, team_sync if the team branch has moved, team_merge, then change_done {id} again.");
        }

        var done = _changes.MarkDone(id, owner.Name, arguments.Summary ?? "");
        return done.Ok
            ? (false, $"Recorded. {done.Request!.From} can team_sync to bring the change for {id} into its worktree.")
            : (true, done.Error!);
    }

    private async Task<(bool IsError, string Text)> AppealChangeAsync(
        OwnerAgent owner, WorkContext work, AppealChangeArguments arguments, CancellationToken cancellationToken)
    {
        var id = arguments.Id?.Trim() ?? "";
        var appealed = _changes.Appeal(id, owner.Name, arguments.Argument ?? "");
        if (!appealed.Ok)
        {
            return (true, appealed.Error!);
        }

        // The lead rules once and for good. Without a ruling the owner's refusal stands: it knows its file.
        var ruling = _conductor is { } lead
            ? await lead.ArbitrateAsync(work.Goal, appealed.Request!, work.Design.Render(), work.Clock, cancellationToken)
            : null;
        if (ruling is { Decided: false, Note: { } why })
        {
            Gap(why);
        }

        var applied = _changes.Arbitrate(
            id,
            ruling?.Ruling.RequesterWins ?? false,
            ruling?.Ruling.Instruction ?? "No ruling could be had, so the owner's refusal stands.");
        if (!applied.Ok)
        {
            return (true, applied.Error!);
        }

        owner.Tell($"{id}:{applied.Request!.State}");
        return (false, ChangeRequestMessages.AppealRuling(applied.Request));
    }

    /// <summary>
    /// A new file the design did not list. Claimed by the owner that needs it, if nobody owns it, nothing is there, and it
    /// is not a file that defines how the solution builds. The map is replaced, never edited, so every dispatcher sees the
    /// new one on its next call; the record is rewritten so it never lags the rules in force.
    /// </summary>
    private async Task<(bool IsError, string Text)> ClaimNewFileAsync(
        OwnerAgent owner, WorkContext work, ClaimNewFileArguments arguments, CancellationToken cancellationToken)
    {
        if (PipelineArtifactStore.NormalizeRelativePath(arguments.Path) is not { } path)
        {
            return (true, $"'{arguments.Path?.Trim()}' is not a plain path relative to the solution folder.");
        }

        if ((arguments.Reason?.Trim().Length ?? 0) < PipelineChangeTools.MinClaimReasonLength)
        {
            return (true, "Say why your work needs this file (a sentence): a claim is a change to who may edit what.");
        }

        if (BuildDefiningFiles.Refusal(path) is { } defining)
        {
            return (true, defining);
        }

        if (_ownership.OwnerOf(path) is { } existingOwner)
        {
            return (true, SameAgent(existingOwner, owner.Name)
                ? $"{path} is already yours."
                : $"{path} is owned by {existingOwner}. Ask them with request_change.");
        }

        var onTeamBranch = await _teamGit.GetFileAtAsync(
            work.Workspace.RepositoryRoot, work.Workspace.TeamBranch, work.Workspace.ToRepositoryPath(path), cancellationToken);
        if (onTeamBranch is not null)
        {
            return (true, $"{path} already exists and the design does not give it to anyone, so it is not a new file. If your work needs "
                          + "it changed, say so in your report.");
        }

        OwnershipMap claimed;
        lock (_ownershipLock)
        {
            var (map, error) = _ownership.TryClaim(owner.Name, path);
            if (map is null)
            {
                return (true, error!);
            }

            _ownership = claimed = map;
        }

        await RecordAsync("ownership.json", OwnershipRecord.From(claimed), cancellationToken);
        return (false, $"Claimed {path}: you may now create and edit it, and you own it for the rest of the run.");
    }

    private static bool SameAgent(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
