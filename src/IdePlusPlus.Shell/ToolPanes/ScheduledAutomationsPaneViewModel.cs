using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class ScheduledAutomationsPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "ScheduledAutomations";

    private readonly ScheduledAutomationService _scheduledAutomationService;
    private readonly CopilotChatPaneViewModel _copilotChat;
    private readonly Dispatcher _dispatcher;

    public ScheduledAutomationsPaneViewModel(
        ScheduledAutomationService scheduledAutomationService, CopilotChatPaneViewModel copilotChat)
        : base(PaneContentId, "Scheduled Automations")
    {
        _scheduledAutomationService = scheduledAutomationService;
        _copilotChat = copilotChat;
        _dispatcher = System.Windows.Application.Current!.Dispatcher;

        isEnabled = _scheduledAutomationService.IsEnabled;

        // Fires from whatever thread a run finished on, or SetSolutionDirectory's caller's thread -
        // never guaranteed to be the UI thread, so the refresh itself has to be dispatched, not just
        // the individual property writes inside it.
        _scheduledAutomationService.SchedulesChanged += () => _dispatcher.Invoke(RefreshSchedules);
        RefreshSchedules();
    }

    public ObservableCollection<ScheduleRowViewModel> Schedules { get; } = [];

    public IReadOnlyList<ScheduleTriggerKind> AvailableTriggerKinds { get; } =
        [ScheduleTriggerKind.Interval, ScheduleTriggerKind.DailyAtTime];

    /// <summary>Off by default (mirrors ScheduledAutomationService.IsEnabled, which this both reads
    /// at startup and writes on every change) - the master switch nothing fires without, the same
    /// "never on by accident" precedent as CopilotChatPaneViewModel.AreShellCommandsEnabled.</summary>
    [ObservableProperty]
    private bool isEnabled;

    [ObservableProperty]
    private string newScheduleName = "";

    [ObservableProperty]
    private string newSchedulePrompt = "";

    [ObservableProperty]
    private ScheduleTriggerKind newScheduleTriggerKind = ScheduleTriggerKind.Interval;

    [ObservableProperty]
    private string newScheduleIntervalHoursText = "24";

    [ObservableProperty]
    private string newScheduleDailyTimeText = "09:00";

    [ObservableProperty]
    private string? statusMessage;

    partial void OnIsEnabledChanged(bool value) => _scheduledAutomationService.IsEnabled = value;

    private void RefreshSchedules()
    {
        Schedules.Clear();
        foreach (var schedule in _scheduledAutomationService.ListSchedules()
                     .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase))
        {
            Schedules.Add(new ScheduleRowViewModel(schedule));
        }
    }

    private bool CanAddSchedule() =>
        !string.IsNullOrWhiteSpace(NewScheduleName) && !string.IsNullOrWhiteSpace(NewSchedulePrompt);

    /// <summary>
    /// Schedules are human-authored only - there is deliberately no agent-callable tool that reaches
    /// this. Provider/model are captured from the chat pane's current selection at the moment this
    /// runs, not re-read later: see ScheduleDefinition's own doc comment for why.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAddSchedule))]
    private void AddSchedule()
    {
        var name = NewScheduleName.Trim();
        if (Schedules.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"A schedule named '{name}' already exists.";
            return;
        }

        if (!double.TryParse(NewScheduleIntervalHoursText, out var intervalHours) || intervalHours <= 0)
        {
            intervalHours = 24;
        }

        if (!TimeSpan.TryParse(NewScheduleDailyTimeText, out var dailyTime))
        {
            dailyTime = TimeSpan.FromHours(9);
        }

        _scheduledAutomationService.SaveSchedule(new ScheduleDefinition(
            name,
            NewScheduleTriggerKind,
            intervalHours,
            dailyTime,
            NewSchedulePrompt.Trim(),
            _copilotChat.SelectedProvider,
            _copilotChat.SelectedModel,
            Enabled: true,
            LastRunUtc: null,
            LastResultSummary: null));

        NewScheduleName = "";
        NewSchedulePrompt = "";
        StatusMessage = null;
    }

    [RelayCommand]
    private void DeleteSchedule(ScheduleRowViewModel row) => _scheduledAutomationService.DeleteSchedule(row.Name);

    [RelayCommand]
    private void ToggleEnabled(ScheduleRowViewModel row) =>
        _scheduledAutomationService.SaveSchedule(row.Source with { Enabled = !row.Enabled });

    [RelayCommand]
    private async Task RunNowAsync(ScheduleRowViewModel row) => await _scheduledAutomationService.RunNowAsync(row.Name);

    partial void OnNewScheduleNameChanged(string value) => AddScheduleCommand.NotifyCanExecuteChanged();

    partial void OnNewSchedulePromptChanged(string value) => AddScheduleCommand.NotifyCanExecuteChanged();
}
