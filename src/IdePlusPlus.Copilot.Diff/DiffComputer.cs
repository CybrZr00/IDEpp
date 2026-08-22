using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace IdePlusPlus.Copilot.Diff;

public static class DiffComputer
{
    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText)
    {
        var model = InlineDiffBuilder.Diff(oldText, newText);
        return model.Lines.Select(line => new DiffLine(line.Text, line.Type switch
        {
            ChangeType.Inserted => DiffLineKind.Added,
            ChangeType.Deleted => DiffLineKind.Removed,
            _ => DiffLineKind.Unchanged,
        })).ToList();
    }
}
