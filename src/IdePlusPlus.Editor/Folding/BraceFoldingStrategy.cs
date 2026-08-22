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
        var newFoldings = ComputeFoldRegions(document.Text).Select(r => new NewFolding(r.Start, r.End)).ToList();
        manager.UpdateFoldings(newFoldings, -1);
    }

    /// <summary>The pure part of the strategy - offset pairs sorted by start offset, with no
    /// dependency on AvalonEdit's FoldingManager, so it's directly unit-testable.</summary>
    public static IReadOnlyList<(int Start, int End)> ComputeFoldRegions(string text)
    {
        var regions = new List<(int Start, int End)>();
        var startOffsets = new Stack<int>();

        for (var offset = 0; offset < text.Length; offset++)
        {
            var c = text[offset];
            if (c == '{')
            {
                startOffsets.Push(offset);
            }
            else if (c == '}' && startOffsets.Count > 0)
            {
                var start = startOffsets.Pop();
                if (offset > start)
                {
                    regions.Add((start, offset + 1));
                }
            }
        }

        regions.Sort((a, b) => a.Start.CompareTo(b.Start));
        return regions;
    }
}
