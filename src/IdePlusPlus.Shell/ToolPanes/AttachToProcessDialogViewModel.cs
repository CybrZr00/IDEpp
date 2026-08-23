using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IdePlusPlus.Debug;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Backs AttachToProcessDialog: lists candidate processes for Attach-to-Process, heuristically
/// flagging likely-.NET ones (per the design's MVP scope) so they sort to the top rather than being
/// filtered out entirely - the heuristic can miss real .NET processes, so nothing is hidden.
/// </summary>
public sealed partial class AttachToProcessDialogViewModel : ObservableObject
{
    public AttachToProcessDialogViewModel()
    {
        Refresh();
    }

    public ObservableCollection<ProcessAttachTarget> Candidates { get; } = [];

    [ObservableProperty]
    private ProcessAttachTarget? selectedCandidate;

    [RelayCommand]
    private void Refresh()
    {
        var previouslySelectedProcessId = SelectedCandidate?.ProcessId;

        var candidates = ProcessAttachTargetFinder.FindCandidates()
            .OrderByDescending(c => c.IsLikelyDotNet)
            .ThenBy(c => c.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Candidates.Clear();
        foreach (var candidate in candidates)
        {
            Candidates.Add(candidate);
        }

        SelectedCandidate = previouslySelectedProcessId is { } processId
            ? Candidates.FirstOrDefault(c => c.ProcessId == processId)
            : null;
    }
}
