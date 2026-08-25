using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Editor;

namespace IdePlusPlus.Copilot.Diff;

public enum ApplyResult
{
    Applied,
    Conflict,

    /// <summary>The target resolved outside the open solution directory, or no solution is open.
    /// Nothing was written.</summary>
    OutsideWorkspace,

    /// <summary>The operation's start_line/end_line do not fit the file as it currently stands.
    /// Nothing was written.</summary>
    InvalidRange,
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

    /// <summary>
    /// Full path if the model's chosen path stays inside the open solution, otherwise null.
    ///
    /// The read side has always been contained (see WorkspaceFileReader, whose whole reason for
    /// existing is that the model picks these paths); the write side was not, and propose_edit's
    /// schema advertised absolute paths. Auto Mode applies edits with no human in the loop, so an
    /// uncontained write path is a prompt-injection primitive, not a theoretical one. Containment
    /// is delegated to WorkspaceFileReader.ResolveWithinRoot rather than reimplemented, so reads
    /// and writes cannot drift apart on what "inside the solution" means.
    ///
    /// A null solution directory refuses rather than falling back to the process working directory
    /// - with no solution open there is no workspace for an edit to be inside of, and read_file
    /// already declines for the same reason.
    /// </summary>
    public static string? ResolveWithinWorkspace(string filePath, string? workspaceDirectory) =>
        workspaceDirectory is null ? null : WorkspaceFileReader.ResolveWithinRoot(workspaceDirectory, filePath);

    public string GetCurrentContent(string fullPath)
    {
        var openDocument = FindOpenDocument(fullPath);
        if (openDocument is not null)
        {
            return openDocument.TextDocument.Text;
        }

        return File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";
    }

    /// <summary>
    /// Computes what the file becomes, or fails with a message the caller can show and the model
    /// can act on.
    ///
    /// This replaced a pair of Math.Clamp calls that turned every bad range into a silent
    /// truncation. start_line/end_line are required only in propose_edit's prose - JSON Schema
    /// cannot express "required when mode is replace_range" in a form every provider honours - so
    /// an omitted end_line arrived as null, clamped to the line count, and deleted from start_line
    /// to EOF while still reporting success. Omitting both replaced the whole file with the
    /// fragment. A stale end_line did the same: in a multi-edit Auto Mode turn nothing renumbers a
    /// file after the previous edit, so the second edit's numbers are routinely out of date.
    ///
    /// There is deliberately no lenient fallback. Guessing at what the model meant is precisely
    /// what made the original data loss invisible.
    /// </summary>
    public static bool TryComputeResultingContent(
        EditOperation operation, string currentContent, out string result, out string? error)
    {
        result = "";

        if (operation.IsWholeFile)
        {
            result = operation.NewText;
            error = null;
            return true;
        }

        var lines = currentContent.Replace("\r\n", "\n").Split('\n').ToList();

        if (operation.StartLine is not { } startLine || operation.EndLine is not { } endLine)
        {
            error = "replace_range requires both start_line and end_line. Use the 1-based line "
                    + "numbers shown by read_file, and pass the same value for both to replace a "
                    + "single line.";
            return false;
        }

        if (startLine < 1 || startLine > lines.Count)
        {
            error = $"start_line {startLine} is outside the file, which has {lines.Count} line(s). "
                    + "Re-read the file for current line numbers - they shift after every edit.";
            return false;
        }

        if (endLine < startLine)
        {
            error = $"end_line {endLine} is before start_line {startLine}. end_line is inclusive, "
                    + "so it must be greater than or equal to start_line.";
            return false;
        }

        if (endLine > lines.Count)
        {
            error = $"end_line {endLine} is past the end of the file, which has {lines.Count} "
                    + "line(s). Re-read the file for current line numbers - they shift after "
                    + "every edit.";
            return false;
        }

        lines.RemoveRange(startLine - 1, endLine - startLine + 1);
        lines.InsertRange(startLine - 1, operation.NewText.Replace("\r\n", "\n").Split('\n'));
        result = string.Join("\n", lines);
        error = null;
        return true;
    }

    /// <summary>
    /// Applies the operation only if the file's content still matches what the diff was computed
    /// against - guards against the file having changed (e.g. the user kept typing) while the
    /// proposal was streaming or awaiting a decision.
    ///
    /// Containment is re-checked here rather than trusted from the caller: this is the method that
    /// actually writes, and it is reachable from more than one place.
    /// </summary>
    public ApplyResult Apply(
        EditOperation operation, string fullPath, string expectedOriginalContent, string? workspaceDirectory)
    {
        if (ResolveWithinWorkspace(fullPath, workspaceDirectory) is null)
        {
            return ApplyResult.OutsideWorkspace;
        }

        var currentContent = GetCurrentContent(fullPath);
        if (currentContent != expectedOriginalContent)
        {
            return ApplyResult.Conflict;
        }

        if (!TryComputeResultingContent(operation, currentContent, out var newContent, out _))
        {
            return ApplyResult.InvalidRange;
        }

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
