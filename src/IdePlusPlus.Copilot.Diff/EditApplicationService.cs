using IdePlusPlus.Editor;

namespace IdePlusPlus.Copilot.Diff;

public enum ApplyResult
{
    Applied,
    Conflict,
}

/// <summary>
/// Resolves an EditOperation's target file and applies it - to the open document's buffer if
/// the file is open, otherwise directly to disk.
/// </summary>
public sealed class EditApplicationService
{
    private readonly EditorWorkspace _editorWorkspace;

    public EditApplicationService(EditorWorkspace editorWorkspace)
    {
        _editorWorkspace = editorWorkspace;
    }

    public string ResolveFullPath(string filePath, string? baseDirectory)
    {
        if (Path.IsPathRooted(filePath))
        {
            return Path.GetFullPath(filePath);
        }

        return Path.GetFullPath(Path.Combine(baseDirectory ?? Directory.GetCurrentDirectory(), filePath));
    }

    public string GetCurrentContent(string fullPath)
    {
        var openDocument = FindOpenDocument(fullPath);
        if (openDocument is not null)
        {
            return openDocument.TextDocument.Text;
        }

        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
    }

    public static string ComputeResultingContent(EditOperation operation, string currentContent)
    {
        if (operation.IsWholeFile)
        {
            return operation.NewText;
        }

        var lines = currentContent.Replace("\r\n", "\n").Split('\n').ToList();
        var startIndex = Math.Clamp((operation.StartLine ?? 1) - 1, 0, lines.Count);
        var endIndex = Math.Clamp(operation.EndLine ?? lines.Count, startIndex, lines.Count);

        lines.RemoveRange(startIndex, endIndex - startIndex);
        lines.InsertRange(startIndex, operation.NewText.Replace("\r\n", "\n").Split('\n'));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Applies the operation only if the file's content still matches what the diff was computed
    /// against - guards against the file having changed (e.g. the user kept typing) while the
    /// proposal was streaming or awaiting a decision.
    /// </summary>
    public ApplyResult Apply(EditOperation operation, string fullPath, string expectedOriginalContent)
    {
        var currentContent = GetCurrentContent(fullPath);
        if (currentContent != expectedOriginalContent)
        {
            return ApplyResult.Conflict;
        }

        var newContent = ComputeResultingContent(operation, currentContent);
        var openDocument = FindOpenDocument(fullPath);
        if (openDocument is not null)
        {
            openDocument.TextDocument.Text = newContent;
        }
        else
        {
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // A file that isn't open has no editor undo stack to fall back on - back up the
            // pre-edit content so an AI-proposed change to a closed file is still recoverable.
            if (File.Exists(fullPath))
            {
                File.Copy(fullPath, fullPath + ".bak", overwrite: true);
            }

            File.WriteAllText(fullPath, newContent);
        }

        return ApplyResult.Applied;
    }

    private DocumentViewModel? FindOpenDocument(string fullPath) =>
        _editorWorkspace.OpenDocuments.FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
}
