namespace IdePlusPlus.Editor;

/// <summary>Result of <see cref="IDefinitionProvider.GetDefinitionAsync"/>. Closed to just these two
/// cases via the private constructor.</summary>
public abstract record EditorDefinitionOutcome
{
    /// <summary>A real declaration site, 1-based like AvalonEdit's own TextEditor.Line/Column -
    /// jump the caret there, opening <paramref name="FilePath"/> first if it isn't the active file.</summary>
    public sealed record Found(string FilePath, int Line, int Column) : EditorDefinitionOutcome;

    /// <summary>Resolved to a real symbol, but with no source in this solution to jump to (a BCL or
    /// NuGet-package member, say).</summary>
    public sealed record NoSourceAvailable(string SymbolDisplayName) : EditorDefinitionOutcome;

    private EditorDefinitionOutcome()
    {
    }
}

/// <summary>
/// Editor-side abstraction over a background live-analysis workspace's position-based symbol
/// resolution (Roslyn's SymbolFinder, in practice - see IdePlusPlus.Roslyn.RoslynDefinitionService).
/// Kept Roslyn-type-free for the same reason as ILiveDiagnosticsProvider/IMemberCompletionProvider.
/// </summary>
public interface IDefinitionProvider
{
    /// <summary>
    /// Resolves the symbol at offset (a 0-based char offset into text, matching AvalonEdit's
    /// TextEditor.CaretOffset) in the given document text, and returns where it's declared. Returns
    /// null rather than throwing when nothing resolves there (whitespace, a keyword, an offset the
    /// workspace hasn't indexed yet) - same "empty rather than throw" contract as
    /// IMemberCompletionProvider.GetCompletionsAsync.
    /// </summary>
    Task<EditorDefinitionOutcome?> GetDefinitionAsync(
        string filePath,
        string text,
        int offset,
        CancellationToken cancellationToken = default);
}
