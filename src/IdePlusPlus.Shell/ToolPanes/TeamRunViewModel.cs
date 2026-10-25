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
    private readonly Func<Task>? _discardBranch;

    public TeamRunViewModel(UltraTeamRun run, bool isLocalProvider, Func<Task>? discardBranch)
    {
        _run = run;
        _discardBranch = discardBranch;
        Workers = new ObservableCollection<TeamWorkerRowViewModel>(run.Workers.Select(w => new TeamWorkerRowViewModel(w.Name)));
        Header = $"⚡ Ultra team - {run.Workers.Count} worker{(run.Workers.Count == 1 ? "" : "s")}";
        LocalProviderWarning = isLocalProvider
            ? "A local model server usually answers one request at a time, so these workers will mostly take turns."
            : null;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    public string Header { get; }

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

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiscardBranchCommand))]
    private string? keptBranch;

    public void Finish(UltraTeamResult result)
    {
        _timer.Stop();
        Refresh();
        IsRunning = false;
        OutcomeText = result.EndReason switch
        {
            UltraTeamEndReason.AllDone => "Finished - every worker reported done.",
            UltraTeamEndReason.TimeUp => "Stopped - the time budget ran out.",
            UltraTeamEndReason.Cancelled => "Stopped.",
            _ => "Stopped - no worker was still making progress.",
        };
        KeptBranch = result.TeamBranchKept && _discardBranch is not null ? result.TeamBranch : null;
    }

    public void Abandon(string why)
    {
        _timer.Stop();
        IsRunning = false;
        OutcomeText = why;
    }

    private bool CanDiscardBranch() => KeptBranch is not null;

    [RelayCommand(CanExecute = nameof(CanDiscardBranch))]
    private async Task DiscardBranchAsync()
    {
        if (_discardBranch is null)
        {
            return;
        }

        await _discardBranch();
        OutcomeText += " Team branch deleted.";
        KeptBranch = null;
    }

    private void Refresh()
    {
        var elapsed = _run.Elapsed;
        var merges = _run.Workers.Sum(w => w.Merges);
        ProgressText = $"{Clock(elapsed)} / {Clock(_run.TimeBudget)}  ·  {merges} merge{(merges == 1 ? "" : "s")}  ·  "
                       + $"{_run.PromptTokens + _run.CompletionTokens:N0} tokens";

        for (var i = 0; i < Workers.Count; i++)
        {
            var state = _run.Workers[i];
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
