using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Folding;

namespace IdePlusPlus.Editor.Folding;

/// <summary>
/// Minimal brace-matching folding for curly-brace languages (C#, JSON). Not
/// syntax-aware — braces inside strings/comments aren't excluded — but good
/// enough for collapsing blocks until a real parser-backed strategy is worth it.
/// </summary>
public sealed class BraceFoldingStrategy
{
    public void UpdateFoldings(FoldingManager manager, TextDocument document)
    {
        var newFoldings = new List<NewFolding>();
        var startOffsets = new Stack<int>();

        for (var offset = 0; offset < document.TextLength; offset++)
        {
            var c = document.GetCharAt(offset);
            if (c == '{')
            {
                startOffsets.Push(offset);
            }
            else if (c == '}' && startOffsets.Count > 0)
            {
                var start = startOffsets.Pop();
                if (offset > start)
                {
                    newFoldings.Add(new NewFolding(start, offset + 1));
                }
            }
        }

        newFoldings.Sort((a, b) => a.StartOffset.CompareTo(b.StartOffset));
        manager.UpdateFoldings(newFoldings, -1);
    }
}
