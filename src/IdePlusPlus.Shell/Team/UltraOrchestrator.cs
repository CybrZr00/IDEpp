using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Embeddings;

namespace IdePlusPlus.Shell.Team;

/// <summary>
/// The lead of an orchestrated Ultra run: plans the work before any worker starts, checks in on the
/// team while it runs, and reviews each merge before it lands.
///
/// It sits on top of the same machinery an unorchestrated run uses - the board, the message bus and
/// team_merge's build gate - and adds three things to it. It writes its plan to the board as TASKs
/// (workers take tasks rather than inventing work), it speaks to workers through the message bus
/// (they hear it between steps), and team_merge asks it to review before fast-forwarding. It has no
/// worktree and no edit tool: it can read code, and everything else it does is one of the four
/// <see cref="LeadTools"/>.
///
/// Every call is a separate short conversation with the lead's own model and effort - there is no
/// long-lived lead context to grow or compact. What it knows it reads from the board and the diff in
/// front of it, the way a worker's cycle starts from the board.
///
/// It must never be the reason a run stops. A failed planning, supervision or review call is reported
/// to the team and the run goes on without that help; a review that cannot be had approves the merge.
/// </summary>
public sealed class UltraOrchestrator
{
    private const int PlanningIterations = 20;
    private const int SupervisionIterations = 6;
    private const int ReviewIterations = 6;

    /// <summary>The diff shown for a review is cut here. A change bigger than this is judged on what
    /// fits plus the stat table, which lists every file.</summary>
    internal const int MaxReviewDiffChars = 24_000;

    private readonly IChatClient _client;
    private readonly string _model;
    private readonly ChatEffort _effort;
    private readonly ReadOnlyToolDispatcher _readOnlyTools;
    private readonly string _solutionDirectory;
    private readonly TeamBoard _board;
    private readonly TeamMessageBus _bus;
    private readonly IReadOnlyList<TeamWorkerState> _workers;
    private readonly TimeProvider _time;
    private readonly int _maxConsecutiveRejections;
    private readonly LeadReviewMode _reviewMode;

    /// <summary>Where the lead's thoughts and replies go, for every conversation it has (planning,
    /// checking in, reviewing a merge). Null: not recorded.</summary>
    private readonly TranscriptRecorder? _transcript;

    /// <summary>One review at a time: the lead is one reader, and two workers merging together would
    /// otherwise have it judge each against a team branch the other is about to move. Advisory mode
    /// skips this - the review no longer holds up the merge, so it has nothing to serialise.</summary>
    private readonly SemaphoreSlim _reviewGate = new(1, 1);

    private readonly Dictionary<string, int> _rejections = new(StringComparer.Ordinal);

    /// <summary>One plan comes back for being too narrow or too deep; the second is taken as it is. A
    /// lead that has read the feedback and still cannot split the work knows the code better than a
    /// shape check does, and a plan never written is worse for the run than a serial one.</summary>
    private bool _planShapeRefused;

    public UltraOrchestrator(
        IChatClient client,
        string model,
        ChatEffort effort,
        ReadOnlyToolDispatcher readOnlyTools,
        string solutionDirectory,
        TeamBoard board,
        TeamMessageBus bus,
        IReadOnlyList<TeamWorkerState> workers,
        TimeProvider time,
        int maxConsecutiveRejections = 2,
        LeadReviewMode reviewMode = LeadReviewMode.Blocking,
        TranscriptRecorder? transcript = null)
    {
        _transcript = transcript;
        _client = client;
        _model = model;
        _effort = effort;
        _readOnlyTools = readOnlyTools;
        _solutionDirectory = solutionDirectory;
        _board = board;
        _bus = bus;
        _workers = workers;
        _time = time;
        _maxConsecutiveRejections = maxConsecutiveRejections;
        _reviewMode = reviewMode;
    }

    /// <summary>The callback that keeps one of the lead's conversations in the transcript, labelled
    /// with what the lead was doing; null when nothing is being recorded.</summary>
    private Action<string?, string>? Recording(string source) =>
        _transcript?.WithSource(source) is { } recorder ? recorder.RecordModelTurn : null;

    /// <summary>What the lead is doing now - "planning", "reviewing w2's merge" - or null when idle.</summary>
    public string? Activity { get; private set; }

    /// <summary>
    /// The most recent advisory review, for a caller that wants to wait for the verdict it did not
    /// wait for at merge time. Null until one has run.
    /// </summary>
    public Task? LastAdvisoryReview { get; private set; }

    public int TasksPlanned { get; private set; }

    public int ReviewsApproved { get; private set; }

    public int ReviewsRejected { get; private set; }

    /// <summary>Something a status display shows has changed.</summary>
    public event Action? Changed;

    // ---- Planning ----

    /// <summary>
    /// Reads the code and splits the goal into TASKs on the board. Returns how many it wrote; zero
    /// (and a note to the team) when it could not plan, in which case the workers choose for
    /// themselves exactly as in an unorchestrated run.
    /// </summary>
    public async Task<int> PlanAsync(string goal, CancellationToken cancellationToken)
    {
        SetActivity("planning");
        try
        {
            var planned = 0;
            var loop = new HeadlessAgentLoop(
                _client, _model, _effort, LeadTools.ForPlanning,
                async (tool, arguments, ct) =>
                {
                    if (tool != LeadTools.SubmitPlanToolName)
                    {
                        return await _readOnlyTools.ExecuteAsync(tool, arguments, _solutionDirectory, ct);
                    }

                    if (planned > 0)
                    {
                        return (true, "The plan is already submitted. Reply with one line and stop.");
                    }

                    var (written, refusal) = WritePlan(arguments);
                    planned = written;
                    return refusal is not null
                        ? (true, refusal)
                        : written == 0
                            ? (true, "The plan had no usable tasks. Call lead_submit_plan again with at least one.")
                            : (false, $"Plan recorded: {written} task(s) are on the board. Reply with one line and stop.");
                },
                onModelTurn: Recording("ultra lead: planning"));

            await loop.RunAsync(
                TeamPrompts.LeadPlanningPrompt(WorkerNames()),
                TeamPrompts.LeadPlanningOpening(goal, _workers.Count),
                PlanningIterations,
                cancellationToken);

            TasksPlanned = planned;
            if (planned == 0)
            {
                _bus.Post(LeadTools.LeadName, "The lead did not produce a plan - choose work from the goal yourselves.");
            }

            return planned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _bus.Post(LeadTools.LeadName, $"The lead could not plan ({Short(ex.Message)}) - choose work from the goal yourselves.");
            return 0;
        }
        finally
        {
            SetActivity(null);
        }
    }

    /// <summary>
    /// Writes the plan onto the board, or says why not. The shape check comes first and writes nothing:
    /// a plan whose first wave is narrower than the team, or one long chain, is exactly the linear plan
    /// this roster exists to avoid, and the lead gets one chance to split it before it costs the run.
    /// </summary>
    private (int Written, string? Refusal) WritePlan(JsonNode? arguments)
    {
        var tasks = (arguments?["tasks"] as JsonArray)?
            .Select(t => t?.GetValue<string>()?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Take(LeadTools.MaxPlanTasks)
            .Select(t => t!)
            .ToList() ?? [];

        // 'after' is one list per task, by the 1-based position of the tasks it waits for. A task can wait only
        // on those written before it, so a plan can never wait on itself in a circle; anything else the lead
        // wrote is left out rather than refusing the plan it spent its tokens on.
        var dependencies = Dependencies(arguments?["after"] as JsonArray, tasks.Count);

        if (ShapeRefusal(tasks.Count, dependencies) is { } refusal)
        {
            return (0, refusal);
        }

        var boardNumbers = new List<int>();
        for (var i = 0; i < tasks.Count; i++)
        {
            var waitsFor = dependencies[i]
                .Select(position => boardNumbers[position])
                .Where(number => number > 0)
                .Distinct()
                .ToList();
            boardNumbers.Add(WriteTask(tasks[i], waitsFor)?.Number ?? 0);
        }

        if (tasks.Count > 0 && arguments?["notes"]?.GetValue<string>() is { Length: > 0 } notes)
        {
            _bus.Post(LeadTools.LeadName, notes);
        }

        return (tasks.Count, null);
    }

    /// <summary>Each task's prerequisites as 0-based positions in the plan, keeping only the ones that
    /// point at an earlier task - a circle would block its own worker forever.</summary>
    private static List<List<int>> Dependencies(JsonArray? afterLists, int count)
    {
        var dependencies = new List<List<int>>(count);
        for (var i = 0; i < count; i++)
        {
            dependencies.Add((afterLists is not null && i < afterLists.Count ? afterLists[i] as JsonArray : null)?
                .Select(n => int.TryParse(n?.ToString(), out var position) ? position : 0)
                .Where(position => position >= 1 && position <= i)
                .Select(position => position - 1)
                .Distinct()
                .ToList() ?? []);
        }

        return dependencies;
    }

    /// <summary>
    /// What is wrong with the plan's shape, or null when it can use the builders the run has: the first
    /// wave is the tasks that wait for nothing, and the chain is the longest run of tasks that wait for
    /// one another. One builder has nothing to fan out to, so only a plan for two or more is judged.
    /// </summary>
    private string? ShapeRefusal(int tasks, IReadOnlyList<IReadOnlyList<int>> dependencies)
    {
        if (_planShapeRefused || tasks < 2)
        {
            return null;
        }

        var builders = BuilderCount();
        if (builders < 2)
        {
            return null;
        }

        var firstWave = dependencies.Count(waitsFor => waitsFor.Count == 0);
        var depth = new int[tasks];
        for (var i = 0; i < tasks; i++)
        {
            depth[i] = 1 + (dependencies[i].Count == 0 ? 0 : dependencies[i].Max(position => depth[position]));
        }

        var chain = depth.Max();
        var problem = firstWave < builders
            ? $"{firstWave} task(s) wait for nothing, so {builders - firstWave} of the {builders} builders would have nothing to start"
            : tasks >= 3 && chain * 2 > tasks
                ? $"it is one chain {chain} deep across {tasks} task(s), so the work is done one thing after another"
                : null;
        if (problem is null)
        {
            return null;
        }

        _planShapeRefused = true;
        return $"Not written yet: the plan does not use the team. {char.ToUpperInvariant(problem[0])}{problem[1..]}. "
               + $"Split the work by file or area so at least {builders} tasks can start at once, and where pieces need "
               + "something in common (a type, an interface, a setting) put it in the first wave as a stub - an empty "
               + "type or a signature the others compile against - then fan the rest out against it. Keep 'after' for "
               + "what truly cannot start before another task is merged. Call lead_submit_plan again with the split plan.";
    }

    /// <summary>How many workers build - the specialists review what the builders merge, so a plan wide
    /// enough for them would be wider than the work needs.</summary>
    private int BuilderCount()
    {
        var builders = _workers.Count(worker => TeamRoles.ForWorkerName(worker.Name) is not { IsSpecialist: true });
        return Math.Max(1, builders);
    }

    private TeamBoardEntry? WriteTask(string text, IReadOnlyList<int>? after = null) => _board.Write(
        LeadTools.LeadName,
        "TASK",
        text.Length <= TeamBoard.MaxPatchSummaryLength ? text : text[..(TeamBoard.MaxPatchSummaryLength - 3)] + "...",
        text.Length <= TeamBoard.MaxPatchSummaryLength ? null : text,
        after).Entry;

    // ---- Supervision ----

    /// <summary>
    /// Checks in on the team every <paramref name="interval"/> until it is cancelled or every worker is
    /// done. Each check is one short conversation: it sees the board and what each worker is doing, and
    /// either redirects someone or changes nothing.
    /// </summary>
    public async Task SuperviseAsync(
        string goal, Func<TimeSpan> timeLeft, TimeSpan interval, CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(interval, _time, cancellationToken);
            if (_workers.All(w => w.IsDone))
            {
                return;
            }

            await CheckInAsync(goal, timeLeft(), cancellationToken);
        }
    }

    internal async Task CheckInAsync(string goal, TimeSpan timeLeft, CancellationToken cancellationToken)
    {
        SetActivity("checking on the team");
        try
        {
            var loop = new HeadlessAgentLoop(
                _client, _model, _effort, LeadTools.ForSupervision,
                async (tool, arguments, ct) => tool switch
                {
                    LeadTools.MessageToolName => SendMessage(arguments),
                    LeadTools.AddTaskToolName => AddTask(arguments),
                    LeadTools.TriageToolName => Triage(arguments),
                    _ => await _readOnlyTools.ExecuteAsync(tool, arguments, _solutionDirectory, ct),
                },
                onModelTurn: Recording("ultra lead: check-in"));

            await loop.RunAsync(
                TeamPrompts.LeadSupervisionPrompt(WorkerNames()),
                TeamPrompts.LeadSupervisionOpening(
                    goal, timeLeft, DescribeWorkers(), _bus.TakeDirect(LeadTools.LeadName), _board.CycleDigest(LeadTools.LeadName),
                    DescribeOpenFindings()),
                SupervisionIterations,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Skipped, not fatal: the next check tries again, and the workers carry on in the meantime.
            _bus.Post(LeadTools.LeadName, $"(The lead's check-in failed: {Short(ex.Message)}.)");
        }
        finally
        {
            SetActivity(null);
        }
    }

    private (bool IsError, string Text) SendMessage(JsonNode? arguments)
    {
        var to = arguments?["to"]?.GetValue<string>()?.Trim();
        var text = arguments?["text"]?.GetValue<string>();
        if (string.Equals(to, "all", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return (true, "lead_message needs 'text'.");
            }

            foreach (var worker in _workers.Where(w => !w.IsDone))
            {
                _bus.SendDirect(LeadTools.LeadName, worker.Name, text);
            }

            return (false, "Sent to every worker still working.");
        }

        var error = _bus.SendDirect(LeadTools.LeadName, to, text);
        return error is null ? (false, $"Sent to {to}.") : (true, error);
    }

    private (bool IsError, string Text) AddTask(JsonNode? arguments)
    {
        var text = arguments?["text"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (true, "lead_add_task needs 'text'.");
        }

        var task = WriteTask(text, TeamBoard.ParseTaskIds(arguments?["after"]));
        if (task is null)
        {
            // Only 'after' can make a board write fail here: a task id that is not on the board.
            return (true, "Not added: 'after' names a task that is not on the board. Use ids such as \"G3\", or leave 'after' out.");
        }

        TasksPlanned++;
        return (false, $"Added to the board as {task.Id}.");
    }

    /// <summary>
    /// The lead's call on one FINDING: promote it into a builder TASK that names it (so the merge that
    /// fixes it answers it), or dismiss it with a reason on the board. Both are ordinary board entries -
    /// a promoted finding is acted on by the same claim/merge protocol as any TASK.
    /// </summary>
    private (bool IsError, string Text) Triage(JsonNode? arguments)
    {
        var id = arguments?["finding"]?.GetValue<string>()?.Trim();
        var decision = arguments?["decision"]?.GetValue<string>()?.Trim().ToLowerInvariant();
        var reason = arguments?["reason"]?.GetValue<string>()?.Trim() ?? "";
        if (decision is not ("promote" or "dismiss"))
        {
            return (true, "lead_triage needs 'decision' of \"promote\" or \"dismiss\", and a one-line reason.");
        }

        var number = int.TryParse(id?.Trim().TrimStart('G', 'g'), out var parsed) ? parsed : 0;
        var entries = _board.Snapshot();
        var finding = entries.FirstOrDefault(e => e.Number == number && e.Type == TeamBoardEntryType.Finding);
        if (finding is null)
        {
            return (true, $"{id ?? "(nothing)"} is not a FINDING on the board. Pass the id of a FINDING, such as G12.");
        }

        if (TeamBoard.IsAnswered(finding, entries))
        {
            return (false, $"{finding.Id} is already answered - a merge fixed it or it was dismissed. Nothing to do.");
        }

        if (decision == "promote")
        {
            var text = $"{finding.Id}: {finding.Text}";
            var detail = finding.Detail;
            if (text.Length > TeamBoard.MaxPatchSummaryLength)
            {
                detail = finding.Text;
                text = text[..(TeamBoard.MaxPatchSummaryLength - 3)] + "...";
            }

            var task = _board.Write(
                LeadTools.LeadName, "TASK", text, detail, after: null, lane: TeamRoles.Builder.NamePrefix).Entry;
            if (task is null)
            {
                return (true, $"{finding.Id} could not be turned into a TASK - write it as one with lead_add_task.");
            }

            TasksPlanned++;
            return (false, $"Promoted {finding.Id} to {task.Id}, for a builder to take.{(reason.Length > 0 ? " " + reason : "")}");
        }

        // Dismissed: an OBSERVED that names the finding, which is what stops it holding up wrap-up, and
        // the reason with it, so a worker that reads the board later knows it was considered.
        var note = $"dismissed {finding.Id}: {reason}";
        _board.Write(
            LeadTools.LeadName, "OBSERVED",
            note.Length <= TeamBoard.MaxTextLength ? note : note[..(TeamBoard.MaxTextLength - 3)] + "...",
            reason.Length > 0 ? reason : null);
        return (false, $"Dismissed {finding.Id} - it no longer holds up wrap-up.");
    }

    /// <summary>The findings no merge has answered and the lead has not dismissed, oldest first - what
    /// the check-in offers the lead to triage. Counted rather than dumped: a lane that posts ten of
    /// them costs the lead one line and a board_grep.</summary>
    private string DescribeOpenFindings()
    {
        var open = TeamBoard.OpenFindings(_board.Snapshot());
        if (open.Count == 0)
        {
            return "(none)";
        }

        var bySeverity = open
            .GroupBy(f => f.Finding.SeverityName, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Count()} {g.Key}")
            .ToList();
        var lines = open
            .Take(12)
            .Select(f => $"  {TeamBoard.Format(f.Entry)} [{f.Finding.SeverityName}]")
            .ToList();
        if (open.Count > lines.Count)
        {
            lines.Add($"  ({open.Count - lines.Count} more - board_grep FINDING)");
        }

        return $"{open.Count} open ({string.Join(", ", bySeverity)}), oldest first:\n{string.Join("\n", lines)}";
    }

    private string DescribeWorkers()
    {
        var builder = new StringBuilder();
        foreach (var worker in _workers)
        {
            builder.Append($"  {worker.Name}: {worker.Phase.ToString().ToLowerInvariant()}")
                .Append($", {worker.Merges} merge(s), {worker.Steps} step(s)");
            if (worker.CurrentClaim is { } claim)
            {
                builder.Append($", on: {claim}");
            }

            if (worker.LastTool is { } last)
            {
                builder.Append($", last: {last}");
            }

            if (worker.DoneSummary is { } done)
            {
                builder.Append($", done: {done}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    // ---- Merge review ----

    /// <summary>
    /// Reviews one worker's commit before it lands. Approves when the lead approves, when the lead
    /// does not say, when the review cannot be had at all, or when this worker's commits have been
    /// turned down <c>maxConsecutiveRejections</c> times running - a lead and a worker that disagree
    /// that long are costing the run more than either could cost it by being wrong.
    ///
    /// In <see cref="LeadReviewMode.Advisory"/> it approves at once and the review runs on: the merge
    /// lands, and a verdict that would have sent it back becomes a FINDING on the board instead. That
    /// is what stops the one lead from being the queue every merge waits in - and with the specialist
    /// lanes reviewing merged code, a concern raised after the merge still reaches the team.
    /// </summary>
    /// <param name="evidence">What the verdict is judged against, quoted in an advisory FINDING -
    /// the merge's own commit, as "branch:&lt;sha&gt;".</param>
    public async Task<(bool Approved, string Feedback)> ReviewMergeAsync(
        string goal, string worker, string diff, CancellationToken cancellationToken, string? evidence = null)
    {
        if (_reviewMode == LeadReviewMode.Advisory)
        {
            LastAdvisoryReview = ReviewInAdvisoryModeAsync(goal, worker, diff, evidence);
            return (true, "");
        }

        await _reviewGate.WaitAsync(cancellationToken);
        try
        {
            var (approved, feedback, error) = await AskLeadAsync(goal, worker, diff, cancellationToken);
            if (error is not null)
            {
                _bus.Post(LeadTools.LeadName, $"(The lead could not review {worker}'s merge - approved without review: {Short(error)}.)");
                return (true, "");
            }

            if (approved is not false)
            {
                _rejections.Remove(worker);
                ReviewsApproved++;
                return (true, "");
            }

            var rejected = _rejections.GetValueOrDefault(worker) + 1;
            if (rejected > _maxConsecutiveRejections)
            {
                _rejections.Remove(worker);
                ReviewsApproved++;
                _bus.Post(LeadTools.LeadName, $"(Merged {worker}'s work after {_maxConsecutiveRejections} rejections - the lead's last concern: {Short(feedback)})");
                return (true, "");
            }

            _rejections[worker] = rejected;
            ReviewsRejected++;
            return (false, string.IsNullOrEmpty(feedback) ? "The lead rejected this without saying why - re-read the diff against your task." : feedback);
        }
        finally
        {
            _reviewGate.Release();
        }
    }

    /// <summary>One short review conversation. Approved is null when the lead gave no verdict, and
    /// Error is set when the conversation could not be had at all.</summary>
    private async Task<(bool? Approved, string Feedback, string? Error)> AskLeadAsync(
        string goal, string worker, string diff, CancellationToken cancellationToken)
    {
        SetActivity($"reviewing {worker}'s merge");
        try
        {
            bool? approved = null;
            var feedback = "";
            var loop = new HeadlessAgentLoop(
                _client, _model, _effort, LeadTools.ForReview,
                async (tool, arguments, ct) =>
                {
                    if (tool != LeadTools.VerdictToolName)
                    {
                        return await _readOnlyTools.ExecuteAsync(tool, arguments, _solutionDirectory, ct);
                    }

                    approved = arguments?["approve"]?.GetValue<bool>() ?? true;
                    feedback = arguments?["feedback"]?.GetValue<string>()?.Trim() ?? "";
                    return (false, "Recorded. Reply with one line and stop.");
                },
                onModelTurn: Recording($"ultra lead: reviewing {worker}'s merge"));

            await loop.RunAsync(
                TeamPrompts.LeadReviewPrompt(),
                TeamPrompts.LeadReviewOpening(goal, worker, OpenTasks(), diff),
                ReviewIterations,
                cancellationToken);

            return (approved, feedback, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, "", ex.Message);
        }
        finally
        {
            SetActivity(null);
        }
    }

    /// <summary>
    /// The advisory review: the merge is already on the team branch, so a rejection is not a block but
    /// a FINDING for a builder, with the lead's concern as its text. It can no longer be re-run in the
    /// run's own token (the worker's step may have ended), so it lives until it finishes.
    /// </summary>
    private async Task ReviewInAdvisoryModeAsync(string goal, string worker, string diff, string? evidence)
    {
        try
        {
            var (approved, feedback, error) = await AskLeadAsync(goal, worker, diff, CancellationToken.None);
            if (error is not null)
            {
                _bus.Post(LeadTools.LeadName, $"(The lead could not review {worker}'s merge - merged without review: {Short(error)}.)");
                return;
            }

            if (approved is not false)
            {
                ReviewsApproved++;
                return;
            }

            ReviewsRejected++;
            var text = $"severity=medium kind=review fix_by=builders evidence={evidence ?? "lead-review"} | "
                       + $"the lead would have sent {worker}'s merge back: "
                       + (string.IsNullOrEmpty(feedback) ? "re-read it against the goal." : feedback);
            _board.Write(
                LeadTools.LeadName, "FINDING",
                text.Length <= TeamBoard.MaxPatchSummaryLength ? text : text[..(TeamBoard.MaxPatchSummaryLength - 3)] + "...",
                string.IsNullOrEmpty(feedback) ? null : feedback,
                after: null, lane: (TeamRoles.ForWorkerName(worker) ?? TeamRoles.Builder).NamePrefix);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _bus.Post(LeadTools.LeadName, $"(The lead's advisory review of {worker}'s merge failed: {Short(ex.Message)}.)");
        }
    }

    private string OpenTasks()
    {
        var entries = _board.Snapshot();
        var tasks = entries.Where(e => e.Type == TeamBoardEntryType.Task).ToList();
        return tasks.Count == 0
            ? "(none)"
            : string.Join("\n", tasks.Select(t => $"  {TeamBoard.Format(t)}"));
    }

    private IReadOnlyList<string> WorkerNames() => [.. _workers.Select(w => w.Name)];

    private void SetActivity(string? activity)
    {
        Activity = activity;
        Changed?.Invoke();
    }

    private static string Short(string text) => text.Length <= 160 ? text : text[..160] + "...";
}
