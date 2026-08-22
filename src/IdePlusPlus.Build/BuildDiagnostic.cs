namespace IdePlusPlus.Build;

public enum BuildDiagnosticSeverity
{
    Warning,
    Error,
}

/// <summary>FilePath is exactly as MSBuild reported it - relative to the build's working directory
/// unless the project itself used an absolute path.</summary>
public sealed record BuildDiagnostic(
    BuildDiagnosticSeverity Severity,
    string FilePath,
    int Line,
    int Column,
    string Code,
    string Message);
