namespace IdePlusPlus.Build;

public enum BuildDiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>Where a BuildDiagnostic row came from - a full MSBuild build/clean/rebuild, or a live
/// Roslyn recompute as the user types. Distinguishing the two lets ErrorListPaneViewModel clear one
/// source (e.g. wiping stale Build rows at the start of a new build) without wiping the other.</summary>
public enum BuildDiagnosticSource
{
    Build,
    Live,
}

/// <summary>FilePath is exactly as MSBuild reported it - relative to the build's working directory
/// unless the project itself used an absolute path.</summary>
public sealed record BuildDiagnostic(
    BuildDiagnosticSeverity Severity,
    string FilePath,
    int Line,
    int Column,
    string Code,
    string Message,
    BuildDiagnosticSource Source = BuildDiagnosticSource.Build);
