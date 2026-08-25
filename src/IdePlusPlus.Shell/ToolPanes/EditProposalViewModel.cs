using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Diff;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class EditProposalViewModel : ObservableObject
{
    private readonly EditApplicationService _applicationService;

    private EditOperation? _operation;
    private string? _fullPath;
    private string? _workspaceDirectory;
    private string? _originalContentSnapshot;

    public EditProposalViewModel(string toolCallId, EditApplicationService applicationService)
    {
        ToolCallId = toolCallId;
        _applicationService = applicationService;
    }

    public string ToolCallId { get; }

    [ObservableProperty]
    private string filePath = "generating...";

    [ObservableProperty]
    private bool isComplete;

    [ObservableProperty]
    private string previewText = "";

    [ObservableProperty]
    private ObservableCollection<DiffLine> diffLines = [];

    [ObservableProperty]
    private bool isDecided;

    [ObservableProperty]
    private string? decisionText;

    /// <summary>True only once Accept() has actually written the change - false for Reject, and
    /// false for a conflict (the file changed since this was proposed). Auto Mode uses this to
    /// decide whether it's safe to let the model keep going past this proposal.</summary>
    public bool WasApplied { get; private set; }

    public void UpdateStreaming(EditProposalEvent proposalEvent)
    {
        if (proposalEvent.FilePathPreview is { } filePathPreview)
        {
            FilePath = filePathPreview;
        }

        if (proposalEvent.NewTextPreview is { } newTextPreview)
        {
            PreviewText = newTextPreview;
        }
    }

    public void Complete(EditProposalEvent proposalEvent, string? baseDirectory)
    {
        _operation = proposalEvent.Operation!;
        FilePath = _operation.FilePath;
        _workspaceDirectory = baseDirectory;
        _fullPath = EditApplicationService.ResolveWithinWorkspace(_operation.FilePath, baseDirectory);

        // Refused outright rather than offered for review: reading the file to diff it would itself
        // be the disclosure half of the problem, and an Accept button that must never be pressed is
        // worse than no button. Decided-on-arrival leaves both commands disabled.
        if (_fullPath is null)
        {
            IsComplete = true;
            IsDecided = true;
            DecisionText = baseDirectory is null
                ? "Refused - no solution is open, so there is nowhere for this edit to go."
                : "Refused - this path is outside the open solution.";
            return;
        }

        _originalContentSnapshot = _applicationService.GetCurrentContent(_fullPath);

        var resultingContent = EditApplicationService.ComputeResultingContent(_operation, _originalContentSnapshot);
        DiffLines = new ObservableCollection<DiffLine>(DiffComputer.Compute(_originalContentSnapshot, resultingContent));
        IsComplete = true;
    }

    private bool CanDecide() => IsComplete && !IsDecided;

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Accept()
    {
        var result = _applicationService.Apply(
            _operation!, _fullPath!, _originalContentSnapshot!, _workspaceDirectory);
        WasApplied = result == ApplyResult.Applied;
        IsDecided = true;
        DecisionText = result switch
        {
            ApplyResult.Applied => "Accepted",
            ApplyResult.OutsideWorkspace => "Refused - this path is outside the open solution.",
            _ => "Couldn't apply - the file changed since this was proposed.",
        };
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Reject()
    {
        IsDecided = true;
        DecisionText = "Rejected";
    }

    partial void OnIsCompleteChanged(bool value)
    {
        AcceptCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsDecidedChanged(bool value)
    {
        AcceptCommand.NotifyCanExecuteChanged();
        RejectCommand.NotifyCanExecuteChanged();
    }
}
