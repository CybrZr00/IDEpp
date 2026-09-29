using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Core;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Renders a streamed propose_plan tool call as an approve/reject checklist. Mirrors
/// EditProposalViewModel's streaming/complete/decide lifecycle, but there's no file to apply -
/// approving hands control back to CopilotChatPaneViewModel via onApprove, which turns Plan Mode
/// off and re-prompts the model to implement the approved plan with the real tools enabled.
/// </summary>
public sealed partial class PlanProposalViewModel : ObservableObject
{
    private readonly Func<PlanProposalViewModel, Task> _onApprove;
    private readonly Func<bool>? _canApprove;

    /// <param name="canApprove">Whether approving may start implementing right now - false while
    /// another turn is streaming, since approving starts a turn of its own and two at once trample
    /// each other's cancellation and output. Re-checked through <see cref="RefreshCanApprove"/>.</param>
    public PlanProposalViewModel(string toolCallId, Func<PlanProposalViewModel, Task> onApprove, Func<bool>? canApprove = null)
    {
        ToolCallId = toolCallId;
        _onApprove = onApprove;
        _canApprove = canApprove;
    }

    public string ToolCallId { get; }

    [ObservableProperty]
    private string summary = "Planning...";

    [ObservableProperty]
    private ObservableCollection<string> steps = [];

    [ObservableProperty]
    private bool isComplete;

    [ObservableProperty]
    private bool isDecided;

    [ObservableProperty]
    private string? decisionText;

    public void UpdateStreaming(PlanProposalEvent planEvent)
    {
        if (planEvent.SummaryPreview is { } summaryPreview)
        {
            Summary = summaryPreview;
        }
    }

    public void Complete(PlanProposalEvent planEvent)
    {
        Summary = planEvent.Plan!.Summary;
        Steps = new ObservableCollection<string>(planEvent.Plan.Steps);
        IsComplete = true;
    }

    private bool CanDecide() => IsComplete && !IsDecided;

    private bool CanApprove() => CanDecide() && (_canApprove?.Invoke() ?? true);

    /// <summary>For the owner to call when whatever canApprove depends on has changed.</summary>
    public void RefreshCanApprove() => ApproveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanApprove))]
    private async Task ApproveAsync()
    {
        // Checked before IsDecided is set, so a refused approve leaves the plan still approvable
        // rather than marked "implementing" with nothing running.
        if (!CanApprove())
        {
            return;
        }

        IsDecided = true;
        DecisionText = "Approved - implementing...";
        await _onApprove(this);
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Reject()
    {
        IsDecided = true;
        DecisionText = "Rejected";
    }

    partial void OnIsCompleteChanged(bool value)
    {
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDecidedChanged(bool value)
    {
        ApproveCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }
}
