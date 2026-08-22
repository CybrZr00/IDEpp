namespace IdePlusPlus.Editor;

/// <summary>Fill-in-the-middle style inline completion: given the text before and after the caret, suggest what comes next.</summary>
public interface IInlineCompletionProvider
{
    Task<string?> GetCompletionAsync(string prefix, string suffix, CancellationToken cancellationToken);
}
