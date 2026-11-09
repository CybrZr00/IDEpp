using System.Text;
using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;

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

    /// <summary>One review at a time: the lead is one reader, and two workers merging together would
    /// otherwise have it judge each against a team branch the other is about to move.</summary>
    private readonly SemaphoreSlim _reviewGate = new(1, 1);

    private readonly Dictionary<string, int> _rejections = new(StringComparer.Ordinal);

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
        int maxConsecutiveRejections = 2)
    {
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
    }

    /// <summary>What the lead is doing now - "planning", "reviewing w2's merge" - or null when idle.</summary>
    public string? Activity { get; private set; }

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

                    planned = WritePlan(arguments);
                    return planned == 0
                        ? (true, "The plan had no usable tasks. Call lead_submit_plan again with at least one.")
                        : (false, $"Plan recorded: {planned} task(s) are on the board. Reply with one line and stop.");
                });

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

    private int WritePlan(JsonNode? arguments)
    {
        var tasks = (arguments?["tasks"] as JsonArray)?
            .Select(t => t?.GetValue<string>()?.Trim())
            .Where(t => !string.IsNullOrEmpty(t))
            .Take(LeadTools.MaxPlanTasks)
            .Select(t => t!)
            .ToList() ?? [];

        foreach (var task in tasks)
        {
            WriteTask(task);
        }

        if (tasks.Count > 0 && arguments?["notes"]?.GetValue<string>() is { Length: > 0 } notes)
        {
            _bus.Post(LeadTools.LeadName, notes);
        }

        return tasks.Count;
    }

    private void WriteTask(string text) => _board.Write(
        LeadTools.LeadName,
        "TASK",
        text.Length <= TeamBoard.MaxPatchSummaryLength ? text : text[..(TeamBoard.MaxPatchSummaryLength - 3)] + "...",
        text.Length <= TeamBoard.MaxPatchSummaryLength ? null : text);

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
                    _ => await _readOnlyTools.ExecuteAsync(tool, arguments, _solutionDirectory, ct),
                });

            await loop.RunAsync(
                TeamPrompts.LeadSupervisionPrompt(WorkerNames()),
                TeamPrompts.LeadSupervisionOpening(
                    goal, timeLeft, DescribeWorkers(), _bus.TakeDirect(LeadTools.LeadName), _board.CycleDigest(LeadTools.LeadName)),
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

        WriteTask(text);
        TasksPlanned++;
        return (false, "Added to the board.");
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
    /// </summary>
    public async Task<(bool Approved, string Feedback)> ReviewMergeAsync(
        string goal, string worker, string diff, CancellationToken cancellationToken)
    {
        await _reviewGate.WaitAsync(cancellationToken);
        try
        {
            SetActivity($"reviewing {worker}'s merge");
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
                });

            try
            {
                await loop.RunAsync(
                    TeamPrompts.LeadReviewPrompt(),
                    TeamPrompts.LeadReviewOpening(goal, worker, OpenTasks(), diff),
                    ReviewIterations,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _bus.Post(LeadTools.LeadName, $"(The lead could not review {worker}'s merge - approved without review: {Short(ex.Message)}.)");
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
            SetActivity(null);
            _reviewGate.Release();
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
