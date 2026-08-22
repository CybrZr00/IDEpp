using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Build;

namespace IdePlusPlus.Shell.ToolPanes;

/// <summary>
/// Structured build errors/warnings, parsed from BuildService's raw output-line callback via
/// BuildDiagnosticParser - additive to the existing raw Output pane, not a replacement for it.
/// </summary>
public sealed partial class ErrorListPaneViewModel : ToolPaneViewModel
{
    public const string PaneContentId = "ErrorList";

    public ErrorListPaneViewModel() : base(PaneContentId, "Error List")
    {
    }

    public ObservableCollection<BuildDiagnostic> Diagnostics { get; } = [];

    public void Clear() => Diagnostics.Clear();

    public void AddIfDiagnostic(string outputLine)
    {
        if (BuildDiagnosticParser.TryParse(outputLine) is { } diagnostic)
        {
            Diagnostics.Add(diagnostic);
        }
    }
}
