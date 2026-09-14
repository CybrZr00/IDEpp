using IdePlusPlus.Editor;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;

namespace IdePlusPlus.Roslyn;

/// <summary>
/// Thin adapter from Roslyn's <see cref="SymbolFinder.FindSymbolAtPositionAsync(Document, int, CancellationToken)"/>
/// onto <see cref="IDefinitionProvider"/>. All Roslyn-typed state (the Document, ISymbol, Location)
/// stays inside this class and RoslynWorkspaceService - only the Editor-side, Roslyn-type-free
/// <see cref="EditorDefinitionOutcome"/> DTO ever crosses back out to IdePlusPlus.Editor.
///
/// Position-based ("what symbol, reference or declaration, sits at this exact offset") rather than
/// the name-string search RoslynWorkspaceService.FindSymbolsAsync/FindReferencesAsync already offer
/// (via SymbolFinder.FindSourceDeclarationsAsync) - that pair is the right tool for a chat tool
/// asking "where is GetStatusAsync", but breaks on overloads, locals, and shadowed names, which is
/// exactly what a caret sitting on one specific piece of code needs to get right.
/// </summary>
public sealed class RoslynDefinitionService(RoslynWorkspaceService workspaceService) : IDefinitionProvider
{
    /// <inheritdoc />
    public async Task<EditorDefinitionOutcome?> GetDefinitionAsync(
        string filePath, string text, int offset, CancellationToken cancellationToken = default)
    {
        var document = await workspaceService.GetDocumentWithTextAsync(filePath, text, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return null;
        }

        ISymbol? symbol;
        try
        {
            symbol = await SymbolFinder.FindSymbolAtPositionAsync(document, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // A handful of malformed/incomplete-syntax positions can throw finding the enclosing
            // token or its symbol - degrade to "nothing resolved" rather than failing the click.
            return null;
        }

        if (symbol is null)
        {
            return null;
        }

        // First source location only, matching this feature's own scope decision: no disambiguation
        // UI in v1 for a partial class/method with more than one declaration site.
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);

        // location.SourceTree/FilePath is null only for a generated tree with no path - vanishingly
        // rare once IsInSource has already excluded metadata - but treated the same as "no source"
        // rather than risking a null FilePath reaching EditorDefinitionOutcome.Found.
        if (location?.SourceTree?.FilePath is not { Length: > 0 } locationFilePath)
        {
            return new EditorDefinitionOutcome.NoSourceAvailable(symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat));
        }

        var span = location.GetLineSpan();
        return new EditorDefinitionOutcome.Found(locationFilePath, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1);
    }
}
