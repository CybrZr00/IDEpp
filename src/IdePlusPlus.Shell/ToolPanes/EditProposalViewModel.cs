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

    /// <summary>Line counts before and after, shown above the diff. The diff itself is scrollable
    /// but only ever shows a window onto a long file, so a rewrite that quietly drops most of a
    /// file looks much like any other edit until you scroll to where the content used to be. This
    /// puts the magnitude on screen without scrolling.</summary>
    [ObservableProperty]
    private string changeSummary = "";

    /// <summary>Set when the edit removes more than half the file, which is the shape silent
    /// truncation takes. Highlights the summary rather than blocking anything - large deletions
    /// are sometimes exactly what was asked for.</summary>
    [ObservableProperty]
    private bool isLargeDeletion;

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

        // A range that doesn't fit the file is refused here rather than diffed. Previously it was
        // clamped, so the proposal that reached this point - and the diff the user reviewed - was
        // already the truncated version.
        if (!EditApplicationService.TryComputeResultingContent(
                _operation, _originalContentSnapshot, out var resultingContent, out var rangeError))
        {
            IsComplete = true;
            IsDecided = true;
            DecisionText = $"Refused - {rangeError}";
            return;
        }

        var diff = DiffComputer.Compute(_originalContentSnapshot, resultingContent);
        DiffLines = new ObservableCollection<DiffLine>(diff);

        var before = CountLines(_originalContentSnapshot);
        var after = CountLines(resultingContent);
        ChangeSummary =
            $"+{diff.Count(l => l.Kind == DiffLineKind.Added)} "
            + $"-{diff.Count(l => l.Kind == DiffLineKind.Removed)} "
            + $"({before} -> {after} lines)";
        IsLargeDeletion = before > 0 && after * 2 < before;

        IsComplete = true;
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Replace("\r\n", "\n").Split('\n').Length;

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
            ApplyResult.InvalidRange => "Refused - the line range no longer fits the file.",
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
