using IdePlusPlus.Editor;
using Microsoft.CodeAnalysis.Completion;

namespace IdePlusPlus.Roslyn;

/// <summary>
/// Thin adapter from Roslyn's <see cref="CompletionService"/> onto <see cref="IMemberCompletionProvider"/>.
/// All Roslyn-typed state (the Document, the CompletionService itself, CompletionItem/CompletionChange)
/// stays inside this class and RoslynWorkspaceService - only the Editor-side, Roslyn-type-free
/// <see cref="CompletionItemData"/> DTO ever crosses back out to IdePlusPlus.Editor.
/// </summary>
public sealed class RoslynCompletionService : IMemberCompletionProvider
{
    private readonly RoslynWorkspaceService _workspaceService;

    public RoslynCompletionService(RoslynWorkspaceService workspaceService)
    {
        _workspaceService = workspaceService;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CompletionItemData>> GetCompletionsAsync(
        string filePath,
        string text,
        int caretOffset,
        CancellationToken cancellationToken = default)
    {
        var document = await _workspaceService.GetDocumentWithTextAsync(filePath, text, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return [];
        }

        var completionService = CompletionService.GetService(document);
        if (completionService is null)
        {
            return [];
        }

        CompletionList completions;
        try
        {
            completions = await completionService
                .GetCompletionsAsync(document, caretOffset, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        if (completions.ItemsList.Count == 0)
        {
            return [];
        }

        var results = new List<CompletionItemData>(completions.ItemsList.Count);
        foreach (var item in completions.ItemsList)
        {
            cancellationToken.ThrowIfCancellationRequested();

            CompletionChange change;
            try
            {
                // The ground truth for what to actually insert - Roslyn completion items often need
                // more than their DisplayText inserted verbatim (different casing picked up from a
                // partially-typed prefix, "()" appended for a method, generic angle brackets, etc),
                // so this - not item.DisplayText - is what backs InsertText/the replacement span.
                change = await completionService.GetChangeAsync(document, item, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A handful of Roslyn completion providers (e.g. some override/snippet providers)
                // can throw computing a change for a specific item - skip that one item rather than
                // failing the whole completion request.
                continue;
            }

            var textChange = change.TextChange;
            results.Add(new CompletionItemData(
                item.DisplayText,
                textChange.NewText ?? item.DisplayText,
                string.IsNullOrEmpty(item.InlineDescription) ? null : item.InlineDescription,
                textChange.Span.Start,
                textChange.Span.Length));
        }

        return results;
    }
}
