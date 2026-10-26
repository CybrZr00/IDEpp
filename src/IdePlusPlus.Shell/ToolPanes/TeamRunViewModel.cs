using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Shell.Team;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class TeamWorkerRowViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private string status = "waiting";

    [ObservableProperty]
    private string? claim;
}

/// <summary>
/// The status card for one Ultra team run: time against the budget, tokens so far, one row per
/// worker, the latest board entries and how many merges have landed.
///
/// Polled once a second rather than driven by UltraTeamRun.Changed: that event fires from worker
/// threads on every tool call and every usage chunk, and the card only needs to look current to a
/// person - a timer on the UI thread reads the run's thread-safe state and is done.
/// </summary>
public sealed partial class TeamRunViewModel : ObservableObject
{
    private const int BoardLines = 8;

    private readonly UltraTeamRun _run;
    private readonly DispatcherTimer _timer;
    private readonly Func<IReadOnlyList<string>, Task>? _discardBranches;
    private IReadOnlyList<string> _keptBranches = [];

    /// <param name="discardBranches">Deletes the given branches - the run's team branch and any kept
    /// worker branches - once the user has finished with them. Null hides the button.</param>
    /// <param name="startedFrom">The branch the run carries on from, shown in the header; null for a
    /// run from the working tree alone.</param>
    /// <param name="runUntilDone">The run goes on until done or a cap, with nobody asked between
    /// rounds - said in the header, since the card is where the user looks while it runs.</param>
    public TeamRunViewModel(
        UltraTeamRun run,
        bool isLocalProvider,
        Func<IReadOnlyList<string>, Task>? discardBranches,
        string? startedFrom = null,
        bool runUntilDone = false)
    {
        _run = run;
        _discardBranches = discardBranches;
        _headerSuffix = (startedFrom is null ? "" : $", carrying on from {startedFrom}")
                        + (runUntilDone ? ", running until done" : "");
        Workers = new ObservableCollection<TeamWorkerRowViewModel>(run.Workers.Select(w => new TeamWorkerRowViewModel(w.Name)));
        header = ComposeHeader();
        LocalProviderWarning = isLocalProvider
            ? "A local model server usually answers one request at a time, so these workers will mostly take turns."
            : null;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    private readonly string _headerSuffix;

    /// <summary>Recomposed when an auto-mode team grows, which is the one thing about it that changes.</summary>
    [ObservableProperty]
    private string header;

    private string ComposeHeader() =>
        $"⚡ Ultra team - {Workers.Count} worker{(Workers.Count == 1 ? "" : "s")}"
        + (_run.IsAutoScaling ? $" (auto mode, up to {_run.MaxWorkers})" : "")
        + _headerSuffix;

    public string? LocalProviderWarning { get; }

    public ObservableCollection<TeamWorkerRowViewModel> Workers { get; }

    public ObservableCollection<string> RecentBoard { get; } = [];

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private bool isRunning = true;

    /// <summary>Set once the user has let this run's workers run any command unasked - kept on the
    /// card for the rest of the run, since nothing else would show it.</summary>
    [ObservableProperty]
    private string? shellNote;

    [ObservableProperty]
    private string? outcomeText;

    /// <summary>What the run left on branches - the team branch, and worker branches holding work
    /// that never reached it - or null once nothing is left.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscardBranchesCommand))]
    private string? keptBranchesText;

    /// <summary>Set while the run's time is up and it waits for the user: keep going for another
    /// round, or finish and review what was merged.</summary>
    [ObservableProperty]
    private bool isAwaitingContinue;

    [ObservableProperty]
    private string continuePrompt = "";

    [ObservableProperty]
    private string continueLabel = "Keep going";

    private TaskCompletionSource<bool>? _continueAnswer;

    /// <summary>
    /// Shows the keep-going / finish choice and completes with the answer - false if the run is
    /// stopped while it waits. UI thread only.
    /// </summary>
    public Task<bool> AskToContinueAsync(TimeSpan round, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _continueAnswer = answer;
        var minutes = Math.Max(1, (int)Math.Round(round.TotalMinutes));
        ContinuePrompt = $"{_run.PauseExplanation ?? "Time is up."}\n\nKeep the team going for another {minutes} minutes, "
                         + "or finish now and review what it merged?";
        ContinueLabel = $"Keep going (+{minutes} min)";
        IsAwaitingContinue = true;
        cancellationToken.Register(() => answer.TrySetResult(false));
        return answer.Task;
    }

    [RelayCommand]
    private void KeepGoing() => AnswerContinue(true);

    [RelayCommand]
    private void FinishNow() => AnswerContinue(false);

    private void AnswerContinue(bool keepGoing)
    {
        IsAwaitingContinue = false;
        _continueAnswer?.TrySetResult(keepGoing);
        _continueAnswer = null;
    }

    public void Finish(UltraTeamResult result)
    {
        _timer.Stop();
        IsAwaitingContinue = false;
        Refresh();
        IsRunning = false;
        OutcomeText = result.EndReason switch
        {
            UltraTeamEndReason.AllDone => "Finished - every worker reported done.",
            UltraTeamEndReason.TimeUp => "Stopped - the time budget ran out.",
            UltraTeamEndReason.Cancelled => "Stopped.",
            UltraTeamEndReason.Stalled => "Paused and finished - the team had stalled: nothing could merge.",
            UltraTeamEndReason.BudgetSpent => "Stopped - the token budget was spent.",
            _ => "Stopped - no worker was still making progress.",
        };
        _keptBranches = [.. result.TeamBranchKept ? [result.TeamBranch] : Array.Empty<string>(), .. result.KeptWorkerBranches];
        KeptBranchesText = _keptBranches.Count == 0 || _discardBranches is null
            ? null
            : result.KeptWorkerBranches.Count == 0
                ? $"Kept: {result.TeamBranch}"
                : $"Kept: {(result.TeamBranchKept ? "the team branch and " : "")}{result.KeptWorkerBranches.Count} worker "
                  + $"branch{(result.KeptWorkerBranches.Count == 1 ? "" : "es")} with unmerged work";
    }

    public void Abandon(string why)
    {
        _timer.Stop();
        IsRunning = false;
        OutcomeText = why;
    }

    private bool CanDiscardBranches() => KeptBranchesText is not null;

    [RelayCommand(CanExecute = nameof(CanDiscardBranches))]
    private async Task DiscardBranchesAsync()
    {
        if (_discardBranches is null)
        {
            return;
        }

        await _discardBranches(_keptBranches);
        OutcomeText += " The run's branches were deleted.";
        KeptBranchesText = null;
    }

    private void Refresh()
    {
        var merges = _run.Workers.Sum(w => w.Merges);
        var time = _run.IsCheckingBuild
            ? "checking the solution builds before starting the team"
            : _run.IsAwaitingContinue
            ? $"{Clock(_run.Elapsed)} elapsed, waiting for you"
            : $"{Clock(_run.Elapsed)} elapsed, {Clock(_run.TimeLeft < TimeSpan.Zero ? TimeSpan.Zero : _run.TimeLeft)} left";
        ProgressText = (_run.Round > 1 ? $"Round {_run.Round}  ·  " : "")
                       + $"{time}  ·  {merges} merge{(merges == 1 ? "" : "s")}  ·  "
                       + $"{_run.PromptTokens + _run.CompletionTokens:N0}"
                       + (_run.MaxTokens > 0 ? $" of {_run.MaxTokens:N0}" : "") + " tokens"
                       + (_run.CachedPromptTokens > 0 && _run.PromptTokens > 0
                           ? $" ({100.0 * _run.CachedPromptTokens / _run.PromptTokens:0}% of input cached)"
                           : "");

        var team = _run.Workers;
        if (team.Count > Workers.Count)
        {
            foreach (var joined in team.Skip(Workers.Count))
            {
                Workers.Add(new TeamWorkerRowViewModel(joined.Name));
            }

            Header = ComposeHeader();
        }

        for (var i = 0; i < Workers.Count; i++)
        {
            var state = team[i];
            Workers[i].Status = state.Phase switch
            {
                TeamWorkerPhase.Waiting => "waiting to start",
                TeamWorkerPhase.Done => $"done - {state.DoneSummary}",
                TeamWorkerPhase.Stopped => state.LastTool is { } last && last.StartsWith("error:", StringComparison.Ordinal)
                    ? $"stopped ({last})"
                    : "stopped",
                _ => $"cycle {state.Cycles}, {state.Steps} steps" + (state.LastTool is { } tool ? $", last: {tool}" : ""),
            };
            Workers[i].Claim = state.CurrentClaim;
        }

        var latest = _run.Board.Snapshot().TakeLast(BoardLines).Select(TeamBoard.Format).ToList();
        if (!latest.SequenceEqual(RecentBoard))
        {
            RecentBoard.Clear();
            foreach (var line in latest)
            {
                RecentBoard.Add(line);
            }
        }
    }

    private static string Clock(TimeSpan span) => $"{(int)span.TotalMinutes}:{span.Seconds:00}";
}
