using DiffPlex;
using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace IdePlusPlus.Copilot.Diff;

/// <summary>
/// Line-level diff with word-level sub-highlighting on modified lines. Built on
/// SideBySideDiffBuilder (line chunker for the outer alignment, word chunker for the sub-diff
/// within a paired old/new line) rather than InlineDiffBuilder, because InlineDiffBuilder never
/// pairs a changed line with its replacement - it only ever reports pure line inserts/deletes, with
/// no "this line became that line" relationship to sub-diff against. SideBySideDiffBuilder's parallel
/// OldText.Lines/NewText.Lines (same length, Imaginary-padded on whichever side lacks a line at a
/// given index) gives exactly that pairing, which is then flattened back into the same
/// remove-then-add rendering the caller already expects, just with Segments populated on modified
/// pairs. Verified against DiffPlex 1.9.0's actual behavior via reflection before relying on it here
/// - its constructor parameter order is (differ, lineChunker, wordChunker), easy to get backwards.
/// </summary>
public static class DiffComputer
{
    private static readonly SideBySideDiffBuilder Builder = new(Differ.Instance, LineChunker.Instance, WordChunker.Instance);

    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText)
    {
        var model = Builder.BuildDiffModel(oldText, newText, ignoreWhitespace: false, ignoreCase: false);
        var lines = new List<DiffLine>();

        for (var i = 0; i < model.OldText.Lines.Count; i++)
        {
            var oldPiece = model.OldText.Lines[i];
            var newPiece = model.NewText.Lines[i];

            if (oldPiece.Type == ChangeType.Unchanged && newPiece.Type == ChangeType.Unchanged)
            {
                lines.Add(new DiffLine(oldPiece.Text ?? "", DiffLineKind.Unchanged));
                continue;
            }

            if (oldPiece.Type == ChangeType.Modified && newPiece.Type == ChangeType.Modified)
            {
                lines.Add(new DiffLine(oldPiece.Text ?? "", DiffLineKind.Removed, ToSegments(oldPiece)));
                lines.Add(new DiffLine(newPiece.Text ?? "", DiffLineKind.Added, ToSegments(newPiece)));
                continue;
            }

            if (oldPiece.Type != ChangeType.Imaginary)
            {
                lines.Add(new DiffLine(oldPiece.Text ?? "", DiffLineKind.Removed));
            }

            if (newPiece.Type != ChangeType.Imaginary)
            {
                lines.Add(new DiffLine(newPiece.Text ?? "", DiffLineKind.Added));
            }
        }

        return lines;
    }

    private static IReadOnlyList<DiffTextSegment>? ToSegments(DiffPiece piece) =>
        piece.SubPieces?.Select(p => new DiffTextSegment(p.Text ?? "", p.Type != ChangeType.Unchanged)).ToList();
}
