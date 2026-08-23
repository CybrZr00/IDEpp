using IdePlusPlus.Editor;
using Microsoft.CodeAnalysis;

namespace IdePlusPlus.Roslyn;

/// <summary>
/// Pure conversion from a Roslyn <see cref="Diagnostic"/> to the Roslyn-type-free
/// <see cref="LiveDiagnostic"/> DTO that crosses into IdePlusPlus.Editor. Pulled out of
/// <see cref="RoslynWorkspaceService"/> as its own static class (rather than a private method)
/// specifically so it can be unit-tested against real <see cref="Diagnostic"/> instances obtained
/// from plain syntax-tree parsing, without needing a live MSBuildWorkspace.
/// </summary>
public static class LiveDiagnosticMapper
{
    /// <summary>
    /// Converts <paramref name="diagnostic"/>'s location into 1-based line/column pairs (matching
    /// AvalonEdit's TextLocation convention - see <see cref="LiveDiagnostic"/>'s remarks), since
    /// Roslyn's own <see cref="FileLinePositionSpan"/> is 0-based.
    /// </summary>
    public static LiveDiagnostic ToLiveDiagnostic(Diagnostic diagnostic, string filePath)
    {
        var span = diagnostic.Location.GetLineSpan();
        return new LiveDiagnostic(
            filePath,
            span.StartLinePosition.Line + 1,
            span.StartLinePosition.Character + 1,
            span.EndLinePosition.Line + 1,
            span.EndLinePosition.Character + 1,
            diagnostic.Severity.ToString(),
            diagnostic.Id,
            diagnostic.GetMessage());
    }
}
