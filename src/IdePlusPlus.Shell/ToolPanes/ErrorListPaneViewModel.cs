using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Build;
using IdePlusPlus.Editor;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Structured build errors/warnings, parsed from BuildService's raw output-line callback via
/// BuildDiagnosticParser - additive to the existing raw Output pane, not a replacement for it.
/// Also holds live Roslyn diagnostics (BuildDiagnosticSource.Live), pushed via UpsertLiveDiagnostics
/// as the user types - the two sources coexist in one list/grid but are cleared independently, since
/// a Build/Clean/Rebuild run must not wipe out live squiggle-backing rows for files that weren't part
/// of that build, and a live recompute for one file must not touch any other row.
/// </summary>
public sealed partial class ErrorListPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "ErrorList";

    public ErrorListPaneViewModel() : base(PaneContentId, "Error List")
    {
    }

    public ObservableCollection<BuildDiagnostic> Diagnostics { get; } = [];

    /// <summary>Clears every diagnostic regardless of source - kept for any caller that genuinely
    /// wants a full reset (e.g. closing the solution); Build/Clean/Rebuild use
    /// <see cref="ClearBuildDiagnostics"/> instead so they don't wipe live Roslyn rows.</summary>
    public void Clear() => Diagnostics.Clear();

    /// <summary>Removes only Source == Build rows, leaving any live Roslyn diagnostics untouched.
    /// Called at the start of Build/Clean/Rebuild, which only ever knows about Build-sourced rows.</summary>
    public void ClearBuildDiagnostics()
    {
        for (var i = Diagnostics.Count - 1; i >= 0; i--)
        {
            if (Diagnostics[i].Source == BuildDiagnosticSource.Build)
            {
                Diagnostics.RemoveAt(i);
            }
        }
    }

    public void AddIfDiagnostic(string outputLine)
    {
        if (BuildDiagnosticParser.TryParse(outputLine) is { } diagnostic)
        {
            Diagnostics.Add(diagnostic);
        }
    }

    /// <summary>Replaces every Source == Live row for <paramref name="filePath"/> with a fresh set
    /// mapped from <paramref name="diagnostics"/> - the complete, replace-in-place list for that one
    /// file (matching ILiveDiagnosticsProvider.DiagnosticsChanged's own contract), leaving Build rows
    /// and every other file's Live rows untouched.</summary>
    public void UpsertLiveDiagnostics(string filePath, IReadOnlyList<LiveDiagnostic> diagnostics)
    {
        for (var i = Diagnostics.Count - 1; i >= 0; i--)
        {
            var existing = Diagnostics[i];
            if (existing.Source == BuildDiagnosticSource.Live
                && string.Equals(existing.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                Diagnostics.RemoveAt(i);
            }
        }

        foreach (var diagnostic in diagnostics)
        {
            Diagnostics.Add(new BuildDiagnostic(
                diagnostic.Severity == "Error" ? BuildDiagnosticSeverity.Error : BuildDiagnosticSeverity.Warning,
                diagnostic.FilePath,
                diagnostic.Line,
                diagnostic.Column,
                diagnostic.Code,
                diagnostic.Message,
                BuildDiagnosticSource.Live));
        }
    }
}
