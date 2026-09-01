using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Editor;
using IdePlusPlus.Editor.Diff;

namespace IdePlusPlus.Shell.ToolPanes;

public sealed partial class EditProposalViewModel : ObservableObject
{
    private readonly EditApplicationService _applicationService;

    private EditOperation? _operation;
    private string? _fullPath;
    private string? _workspaceDirectory;
    private string? _originalContentSnapshot;
    private int _addedLines;
    private int _removedLines;
    private int _linesBefore;
    private int _linesAfter;

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

    /// <summary>True once this proposal is complete and its resolved target lies inside the given
    /// absolute directory - used to hold specific paths (e.g. saved skills) back from Auto Mode's
    /// auto-accept regardless of IsLargeDeletion. Compared with a trailing separator so a
    /// same-prefixed sibling directory can't pass as "inside".</summary>
    public bool TargetsPathUnder(string absoluteDirectory)
    {
        if (_fullPath is null)
        {
            return false;
        }

        var normalizedDirectory = Path.GetFullPath(absoluteDirectory);
        var withSeparator = normalizedDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedDirectory
            : normalizedDirectory + Path.DirectorySeparatorChar;

        return _fullPath.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Overrides the default "waiting for accept/reject" wording set by Complete() when a
    /// proposal is deliberately held back from Auto Mode's auto-accept for a reason more specific
    /// than IsLargeDeletion - explains why to the model, the same way the large-deletion wording
    /// already does. A no-op once the proposal has already been decided.</summary>
    public void ExplainHeldForManualReview(string reason)
    {
        if (!IsDecided)
        {
            ToolResultText =
                $"Not applied yet: {reason}, so it is waiting for the user to confirm rather than "
                + "being applied automatically.";
        }
    }

    /// <summary>The raw arguments the model sent, replayed into chat history so this appears as a
    /// real propose_edit tool call rather than something that happened off to the side.</summary>
    public string? ArgumentsJson { get; private set; }

    /// <summary>
    /// What the model is told came of this edit. Every propose_edit call gets one: it is the tool
    /// result for the replayed call, so omitting one would leave a tool call unanswered and make
    /// the next request malformed.
    ///
    /// Applied results carry the line counts deliberately. A model that removed 380 lines it did
    /// not mean to remove has no other way to notice, and the reminder that line numbers have moved
    /// is what stops the next edit in the same turn being computed against a file that no longer
    /// looks like that.
    /// </summary>
    public string ToolResultText { get; private set; } =
        "This edit was cut off before it was complete, so nothing was written. Send it again.";

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
        ArgumentsJson = proposalEvent.ArgumentsJson;
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
            ToolResultText = baseDirectory is null
                ? "Not applied: no solution is open, so there is nowhere for this edit to go."
                : $"Not applied: {_operation.FilePath} is outside the open solution directory. "
                  + "Only files inside the open solution can be edited.";
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
            ToolResultText = $"Not applied to {_operation.FilePath}: {rangeError}";
            return;
        }

        var diff = DiffComputer.Compute(_originalContentSnapshot, resultingContent);
        DiffLines = new ObservableCollection<DiffLine>(diff);

        _addedLines = diff.Count(l => l.Kind == DiffLineKind.Added);
        _removedLines = diff.Count(l => l.Kind == DiffLineKind.Removed);
        _linesBefore = CountLines(_originalContentSnapshot);
        _linesAfter = CountLines(resultingContent);
        ChangeSummary = $"+{_addedLines} -{_removedLines} ({_linesBefore} -> {_linesAfter} lines)";
        IsLargeDeletion = _linesBefore > 0 && _linesAfter * 2 < _linesBefore;

        ToolResultText = IsLargeDeletion
            ? $"Not applied yet: this edit removes most of {_operation.FilePath} "
              + $"({_linesBefore} lines down to {_linesAfter}), so it is waiting for the user to "
              + "confirm rather than being applied automatically. If that much deletion was not "
              + "what you intended, say so and propose a smaller edit."
            : $"Waiting for the user to accept or reject this edit to {_operation.FilePath}. "
              + "Nothing has been written yet.";

        IsComplete = true;
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.Replace("\r\n", "\n").Split('\n').Length;

    /// <summary>Plain-text rendering of this proposal for the chat pane's copy-message button - the
    /// diff itself is otherwise only ever shown as styled, unselectable segments.</summary>
    public string BuildPlainTextSummary()
    {
        var builder = new StringBuilder();
        builder.AppendLine(FilePath);
        if (ChangeSummary.Length > 0)
        {
            builder.AppendLine(ChangeSummary);
        }

        foreach (var line in DiffLines)
        {
            var prefix = line.Kind switch
            {
                DiffLineKind.Added => "+ ",
                DiffLineKind.Removed => "- ",
                _ => "  ",
            };
            builder.AppendLine(prefix + line.Text);
        }

        return builder.ToString();
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
            ApplyResult.InvalidRange => "Refused - the line range no longer fits the file.",
            _ => "Couldn't apply - the file changed since this was proposed.",
        };
        ToolResultText = result switch
        {
            ApplyResult.Applied =>
                $"Applied to {FilePath}. {_addedLines} line(s) added, {_removedLines} removed; the "
                + $"file is now {_linesAfter} line(s) long (was {_linesBefore}). Every line number "
                + "below the edit has moved, so re-read the file before using start_line/end_line "
                + "on it again.",
            ApplyResult.OutsideWorkspace =>
                $"Not applied: {FilePath} is outside the open solution directory.",
            ApplyResult.InvalidRange =>
                $"Not applied to {FilePath}: the line range no longer fits the file. Re-read it for "
                + "current line numbers.",
            _ =>
                $"Not applied to {FilePath}: the file changed after this edit was proposed. Re-read "
                + "it and send the edit again against its current contents.",
        };
    }

    [RelayCommand(CanExecute = nameof(CanDecide))]
    private void Reject()
    {
        IsDecided = true;
        DecisionText = "Rejected";
        ToolResultText =
            $"The user rejected this edit to {FilePath}; nothing was written. Do not apply it again "
            + "unless they ask for it.";
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
