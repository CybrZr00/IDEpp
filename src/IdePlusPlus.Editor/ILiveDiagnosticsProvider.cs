namespace IdePlusPlus.Editor;

/// <summary>
/// One compiler diagnostic (error/warning/info) for a single file, as reported by whatever live
/// analysis engine backs <see cref="ILiveDiagnosticsProvider"/> (Roslyn, in practice) - deliberately
/// free of any Microsoft.CodeAnalysis types so IdePlusPlus.Editor never needs to reference the
/// Roslyn package graph. Line/Column are 1-based, matching AvalonEdit's TextLocation convention.
/// </summary>
public sealed record LiveDiagnostic(
    string FilePath,
    int Line,
    int Column,
    int EndLine,
    int EndColumn,
    string Severity,
    string Code,
    string Message);

/// <summary>
/// Editor-side abstraction over a background live-analysis workspace (Roslyn's MSBuildWorkspace, in
/// practice - see IdePlusPlus.Roslyn.RoslynWorkspaceService). Kept Roslyn-type-free so
/// IdePlusPlus.Editor doesn't need to reference the heavy Microsoft.CodeAnalysis.* package graph;
/// TextEditorHost reaches the real implementation through the EditorServices ambient locator, the
/// same pattern used for IInlineCompletionProvider.
/// </summary>
public interface ILiveDiagnosticsProvider
{
    /// <summary>Raised whenever a fresh diagnostic list is available for a file - the second
    /// argument is the complete, replace-in-place list of diagnostics for that file (not a delta).</summary>
    event Action<string, IReadOnlyList<LiveDiagnostic>>? DiagnosticsChanged;

    /// <summary>Call once when a document tab is opened, with its full current text.</summary>
    void NotifyDocumentOpened(string filePath, string text);

    /// <summary>Call on every (debounced) text change while a document is open.</summary>
    void NotifyDocumentChanged(string filePath, string text);

    /// <summary>Call when a document tab is closed. MVP no-op - see RoslynWorkspaceService.</summary>
    void NotifyDocumentClosed(string filePath);
}
