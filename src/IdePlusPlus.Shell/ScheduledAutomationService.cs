using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.LmStudio;
using IdePlusPlus.Copilot.Memory;
using IdePlusPlus.Copilot.OpenRouter;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Shell.ToolPanes;

namespace IdePlusPlus.Shell;

/// <summary>
/// Fires human-authored schedules while IDE++ happens to be running - not truly headless (there is
/// no way to wake the app when it's closed today), but the schedule storage/trigger types
/// (ScheduleDefinition, IScheduleTrigger) carry no WPF dependency, so a future headless/Task
/// Scheduler entry point could reuse them directly from a different Main() without any change here.
///
/// Deliberately does not reuse CopilotChatPaneViewModel for a scheduled run: that view model is a
/// single-conversation singleton with no concurrency protection (IsStreaming is a plain bool,
/// _streamCancellation a single field) - a scheduled run firing mid-conversation would corrupt both
/// runs' state and interleave scheduled output into the visible transcript. Each run gets its own
/// HeadlessAgentLoop instead, restricted to the same read-only tool set investigate_parallel's
/// sub-agents get (see SubAgentTools) - build_solution/run_tests stay out deliberately, since both
/// save dirty documents as a side effect, and doing that from an unattended 2am timer means silently
/// writing out whatever the user has mid-typed in a live, unsaved editor buffer.
/// </summary>
public sealed class ScheduledAutomationService : IDisposable
{
    private const string EnabledSettingKey = "ScheduledAutomationsEnabled";
    private const int MaxIterationsPerRun = 15;
    private const int LastResultSummaryMaxChars = 400;

    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    private const string ScheduleSystemPrompt =
        "You are a scheduled automation running inside the IDE++ IDE, unattended - no user is present " +
        "to answer questions or approve anything. You can read files, list directories, search the " +
        "codebase, look up C# symbols and their references, and check a file for compiler errors. You " +
        "cannot edit files, run commands, or build/test the solution. If the task genuinely cannot be " +
        "completed with these, say so plainly rather than guessing. Your final answer is saved as this " +
        "run's result - write it for someone reading it later, not as a reply to anyone.";

    private readonly AppSettingsStore _appSettingsStore;
    private readonly ScheduleStore _scheduleStore;
    private readonly OpenRouterChatClient _openRouterClient;
    private readonly LmStudioChatClient _lmStudioClient;
    private readonly ReadOnlyToolDispatcher _readOnlyToolDispatcher;
    private readonly Timer _timer;

    private string? _solutionDirectory;
    private volatile bool _isTicking;

    /// <summary>Raised from whatever thread a run happened to finish on (the timer's thread pool, or
    /// whichever thread called RunNowAsync) - never the UI thread. A subscriber that touches UI-bound
    /// state must marshal to it itself, the same convention DebugSessionManager's events use.</summary>
    public event Action? SchedulesChanged;

    public ScheduledAutomationService(
        AppSettingsStore appSettingsStore,
        ScheduleStore scheduleStore,
        OpenRouterChatClient openRouterClient,
        LmStudioChatClient lmStudioClient,
        ReadOnlyToolDispatcher readOnlyToolDispatcher)
    {
        _appSettingsStore = appSettingsStore;
        _scheduleStore = scheduleStore;
        _openRouterClient = openRouterClient;
        _lmStudioClient = lmStudioClient;
        _readOnlyToolDispatcher = readOnlyToolDispatcher;
        _timer = new Timer(_ => _ = TickAsync(), null, TickInterval, TickInterval);
    }

    /// <summary>Off by default, the same "never on by accident" precedent as
    /// CopilotChatPaneViewModel.AreShellCommandsEnabled - the timer keeps running either way, but
    /// TickAsync no-ops entirely while this is false.</summary>
    public bool IsEnabled
    {
        get => bool.TryParse(_appSettingsStore.Get(EnabledSettingKey), out var value) && value;
        set => _appSettingsStore.Set(EnabledSettingKey, value.ToString());
    }

    public void SetSolutionDirectory(string? solutionDirectory)
    {
        _solutionDirectory = solutionDirectory;
        SchedulesChanged?.Invoke();
    }

    public IReadOnlyList<ScheduleDefinition> ListSchedules() =>
        _solutionDirectory is { } solutionDirectory ? _scheduleStore.Load(solutionDirectory) : [];

    /// <summary>Adds a new schedule, or replaces an existing one of the same name - a schedule name
    /// is its identity, the same way a skill's filename is.</summary>
    public void SaveSchedule(ScheduleDefinition schedule)
    {
        if (_solutionDirectory is not { } solutionDirectory)
        {
            return;
        }

        var schedules = _scheduleStore.Load(solutionDirectory)
            .Where(s => s.Name != schedule.Name)
            .Append(schedule)
            .ToList();
        _scheduleStore.Save(solutionDirectory, schedules);
        SchedulesChanged?.Invoke();
    }

    public void DeleteSchedule(string name)
    {
        if (_solutionDirectory is not { } solutionDirectory)
        {
            return;
        }

        var schedules = _scheduleStore.Load(solutionDirectory).Where(s => s.Name != name).ToList();
        _scheduleStore.Save(solutionDirectory, schedules);
        SchedulesChanged?.Invoke();
    }

    public async Task RunNowAsync(string name, CancellationToken cancellationToken = default)
    {
        if (_solutionDirectory is not { } solutionDirectory)
        {
            return;
        }

        if (_scheduleStore.Load(solutionDirectory).FirstOrDefault(s => s.Name == name) is { } schedule)
        {
            await RunScheduleAsync(solutionDirectory, schedule, cancellationToken);
        }
    }

    /// <summary>
    /// Checked every TickInterval. Re-entrancy is guarded at the tick level (a tick already running
    /// skips a new one) rather than per-schedule - runs are expected to be much shorter than
    /// TickInterval, and a schedule with a multi-minute run is exactly the case where letting the
    /// next tick queue up behind it, rather than overlap it, is the right call.
    /// </summary>
    private async Task TickAsync()
    {
        if (_isTicking || !IsEnabled || _solutionDirectory is not { } solutionDirectory)
        {
            return;
        }

        _isTicking = true;
        try
        {
            var now = DateTimeOffset.Now;
            var due = _scheduleStore.Load(solutionDirectory)
                .Where(s => s.Enabled && BuildTrigger(s).ShouldFireAsOf(now, s.LastRunUtc))
                .ToList();

            foreach (var schedule in due)
            {
                await RunScheduleAsync(solutionDirectory, schedule, CancellationToken.None);
            }
        }
        finally
        {
            _isTicking = false;
        }
    }

    private async Task RunScheduleAsync(string solutionDirectory, ScheduleDefinition schedule, CancellationToken cancellationToken)
    {
        IChatClient client = schedule.Provider == CopilotChatPaneViewModel.ProviderLmStudio ? _lmStudioClient : _openRouterClient;
        var loop = new HeadlessAgentLoop(
            client,
            schedule.Model,
            ChatEffort.None,
            SubAgentTools.AllowedTools,
            (toolName, arguments, ct) => _readOnlyToolDispatcher.ExecuteAsync(toolName, arguments, solutionDirectory, ct));

        string resultText;
        try
        {
            var result = await loop.RunAsync(ScheduleSystemPrompt, schedule.Prompt, MaxIterationsPerRun, cancellationToken);
            resultText = result.HitIterationCap
                ? $"{result.FinalText}\n\n[may be incomplete - hit its iteration limit]"
                : result.FinalText;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            resultText = $"Failed: {ex.Message}";
        }

        _scheduleStore.SaveRunLog(solutionDirectory, schedule.Name, resultText);

        var updated = schedule with { LastRunUtc = DateTimeOffset.UtcNow, LastResultSummary = Truncate(resultText) };
        var schedules = _scheduleStore.Load(solutionDirectory)
            .Select(s => s.Name == schedule.Name ? updated : s)
            .ToList();
        _scheduleStore.Save(solutionDirectory, schedules);

        SchedulesChanged?.Invoke();
    }

    private static IScheduleTrigger BuildTrigger(ScheduleDefinition schedule) => schedule.TriggerKind switch
    {
        ScheduleTriggerKind.DailyAtTime => new DailyAtTimeTrigger(schedule.DailyTimeOfDay),
        _ => new IntervalTrigger(schedule.IntervalHours),
    };

    private static string Truncate(string text) =>
        text.Length <= LastResultSummaryMaxChars ? text : text[..LastResultSummaryMaxChars] + "…";

    public void Dispose() => _timer.Dispose();
}
