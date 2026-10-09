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

/// <summary>What <see cref="EditApplicationService.SaveWithinWorkspace"/> did with each file it was
/// asked about. Paths are full paths, except <see cref="Refused"/>, which echoes the request as given
/// (it never resolved to anything).</summary>
/// <param name="Saved">Written to disk.</param>
/// <param name="AlreadySaved">Open, but the editor's text already matched disk.</param>
/// <param name="NotOpen">Not open in the editor, so there is no unsaved change to write.</param>
/// <param name="Refused">Outside the open solution.</param>
/// <param name="Failed">The write threw; the document is still dirty.</param>
public sealed record SaveReport(
    IReadOnlyList<string> Saved,
    IReadOnlyList<string> AlreadySaved,
    IReadOnlyList<string> NotOpen,
    IReadOnlyList<string> Refused,
    IReadOnlyList<(string Path, string Error)> Failed);

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

    /// <summary>
    /// The editor's text for a file open with unsaved changes, otherwise null - so a caller outside
    /// this assembly (WorkspaceFileReader, which cannot reference the editor) can show the model
    /// the same content an edit would be applied to.
    ///
    /// Applying an edit to an open file mutates its buffer and never writes to disk, so disk and
    /// buffer diverge the moment the first edit lands. A file that is open but clean is skipped:
    /// its buffer and the file on disk are the same text.
    /// </summary>
    public string? TryGetUnsavedText(string fullPath) =>
        FindOpenDocument(fullPath) is { IsDirty: true } document ? document.TextDocument.Text : null;

    /// <summary>Whether fullPath already has an open, editor-backed document - used by the
    /// propose_edit review flow (EditProposalViewModel) to tell whether opening the file for
    /// review is itself what makes Apply's buffer-only branch kick in below, so it knows when it
    /// must also flush straight to disk to preserve the "a closed file's accepted edit is durable
    /// immediately" guarantee that would otherwise only apply while the file stayed closed.</summary>
    public bool IsOpen(string fullPath) => FindOpenDocument(fullPath) is not null;

    /// <summary>Whether this particular document is still open - not just some document for the same
    /// path, which after a close and reopen is a different buffer.</summary>
    public bool IsOpen(DocumentViewModel document) => _editorWorkspace.OpenDocuments.Contains(document);

    /// <summary>Writes one document's current buffer to disk - used by EditProposalViewModel.Accept
    /// right after Apply, for a document it opened itself purely to show the diff for review (see
    /// IsOpen above). Not exposed more broadly than that: for a document the user already had open
    /// independently, forcing a save on every AI edit would remove their ability to review/undo it
    /// in the editor before it hits disk, which is the whole reason Apply leaves an open document's
    /// edit buffer-only in the first place.</summary>
    public void SaveDocument(DocumentViewModel document) => _editorWorkspace.Save(document);

    /// <summary>
    /// Writes every open document with unsaved changes to disk, returning the paths written.
    ///
    /// An applied edit to an open file only mutates its buffer - nothing here writes it out - but a
    /// build compiles disk. Without this, a build started straight after an edit reports errors
    /// against source the agent has already fixed, and misses the ones it has just introduced,
    /// which is worse than no build feedback at all. Untitled documents are skipped: there is
    /// nowhere to write them and they are not part of any project.
    /// </summary>
    public IReadOnlyList<string> SaveDirtyDocuments()
    {
        var saved = new List<string>();
        foreach (var document in _editorWorkspace.OpenDocuments.Where(d => d is { IsDirty: true, FilePath: not null }).ToList())
        {
            _editorWorkspace.Save(document);
            saved.Add(document.FilePath!);
        }

        return saved;
    }

    /// <summary>
    /// The model-facing save: writes open documents with unsaved changes to disk, but only ones that
    /// resolve inside the open solution, and reports every file's outcome instead of throwing.
    ///
    /// Narrower than <see cref="SaveDirtyDocuments"/> on purpose. That one exists to feed a build and
    /// saves anything the user has open; this one is callable at the model's discretion, so it
    /// inherits the same containment as the write path (a dirty file outside the solution is left
    /// alone and reported), never creates a file that is not already open, and survives a locked or
    /// read-only target - a failed write leaves that document dirty, so nothing is lost and the next
    /// attempt (or the user's own Save) still has the change.
    ///
    /// <paramref name="paths"/> null means every dirty open document; otherwise exactly those files.
    /// </summary>
    public SaveReport SaveWithinWorkspace(IReadOnlyList<string>? paths, string workspaceDirectory)
    {
        var saved = new List<string>();
        var alreadySaved = new List<string>();
        var notOpen = new List<string>();
        var refused = new List<string>();
        var failed = new List<(string Path, string Error)>();

        var targets = paths is null
            ? _editorWorkspace.OpenDocuments.Where(d => d is { IsDirty: true, FilePath: not null })
                .Select(d => d.FilePath!).ToList()
            : paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets)
        {
            if (ResolveWithinWorkspace(target, workspaceDirectory) is not { } fullPath)
            {
                refused.Add(target);
                continue;
            }

            if (!done.Add(fullPath))
            {
                continue; // The same file named twice, or once relative and once absolute.
            }

            if (FindOpenDocument(fullPath) is not { } document)
            {
                notOpen.Add(fullPath);
            }
            else if (!document.IsDirty)
            {
                alreadySaved.Add(fullPath);
            }
            else
            {
                try
                {
                    _editorWorkspace.Save(document);
                    saved.Add(fullPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add((fullPath, ex.Message));
                }
            }
        }

        return new SaveReport(saved, alreadySaved, notOpen, refused, failed);
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

        if (FindElisionMarker(operation.NewText) is { } elision)
        {
            error = $"new_text contains what looks like a placeholder for omitted content: \"{elision}\". "
                    + "Whatever you send is written to disk exactly as given, so this would delete the "
                    + "code it stands in for. Send the real content, or use replace_text to change only "
                    + "the part you mean to change.";
            return false;
        }

        if (operation.IsWholeFile)
        {
            result = operation.NewText;
            error = null;
            return true;
        }

        var normalizedContent = currentContent.Replace("\r\n", "\n");

        if (operation.IsReplaceText)
        {
            return TryReplaceText(operation, normalizedContent, out result, out error);
        }

        var lines = normalizedContent.Split('\n').ToList();

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
    /// Swaps one exact snippet for another, requiring it to appear exactly once.
    ///
    /// This is the mode that cannot go stale. Line numbers are a reference into a file that the
    /// previous edit already moved, and nothing renumbers between edits in a turn - an anchor is a
    /// reference into the content itself, so it either still matches or it visibly does not. Zero
    /// matches and several matches are both refused rather than resolved by picking one: the whole
    /// class of bug being fixed here is an edit that guessed and reported success.
    /// </summary>
    private static bool TryReplaceText(
        EditOperation operation, string normalizedContent, out string result, out string? error)
    {
        result = "";

        if (operation.OldText is not { Length: > 0 } rawOldText)
        {
            error = "replace_text requires a non-empty old_text: the exact snippet to replace, "
                    + "copied from read_file without the line-number prefixes.";
            return false;
        }

        var oldText = rawOldText.Replace("\r\n", "\n");
        var occurrences = CountOccurrences(normalizedContent, oldText);

        if (occurrences == 0)
        {
            error = "old_text was not found in the file. Copy it verbatim from read_file, without "
                    + "the leading line-number prefixes, and check the file has not changed since "
                    + "you read it.";
            return false;
        }

        if (occurrences > 1)
        {
            error = $"old_text appears {occurrences} times in the file, so it is ambiguous. Include "
                    + "more surrounding lines to make it match exactly one place.";
            return false;
        }

        var index = normalizedContent.IndexOf(oldText, StringComparison.Ordinal);
        result = string.Concat(
            normalizedContent.AsSpan(0, index),
            operation.NewText.Replace("\r\n", "\n"),
            normalizedContent.AsSpan(index + oldText.Length));
        error = null;
        return true;
    }

    /// <summary>
    /// The line that looks like "... rest of file unchanged ...", or null.
    ///
    /// Models abbreviate long files this way, and every mode here writes new_text verbatim, so the
    /// placeholder replaces the code it stands for. Both halves are required - a comment marker with
    /// an ellipsis AND a phrase about omitted content - because either alone is ordinary prose that
    /// appears in real source. A legitimate deletion is expressed by leaving the content out, never
    /// by describing it.
    /// </summary>
    private static string? FindElisionMarker(string newText)
    {
        string[] commentStarts = ["//", "#", "<!--", "/*", "*", "--", "'", ";"];
        string[] phrases =
        [
            "rest of", "unchanged", "omitted", "remains the same", "same as before",
            "existing code", "previous code", "as above", "snip",
        ];

        foreach (var rawLine in newText.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.Contains("...", StringComparison.Ordinal) && !line.Contains('…'))
            {
                continue;
            }

            if (!commentStarts.Any(c => line.StartsWith(c, StringComparison.Ordinal)))
            {
                continue;
            }

            if (phrases.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase)))
            {
                return line.Length > 80 ? line[..80] + "..." : line;
            }
        }

        return null;
    }

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : text.ReplaceLineEndings("\n").Split('\n').Length;

    /// <summary>Counts overlapping occurrences too - an anchor that matches "aa" twice inside "aaa"
    /// is ambiguous in exactly the way this needs to catch.</summary>
    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
        EditOperation operation, string fullPath, string expectedOriginalContent, string? workspaceDirectory) =>
        Apply(operation, fullPath, expectedOriginalContent, workspaceDirectory, out _);

    /// <summary>
    /// As above, and on a refusal says why in words the model can act on: what the file looks like
    /// now (its current length) next to what the edit assumed, so the agent can re-read and re-plan
    /// rather than guess. Null when the edit applied or the reason is fully covered by the result.
    /// </summary>
    public ApplyResult Apply(
        EditOperation operation, string fullPath, string expectedOriginalContent, string? workspaceDirectory,
        out string? failureDetail)
    {
        failureDetail = null;
        if (ResolveWithinWorkspace(fullPath, workspaceDirectory) is null)
        {
            return ApplyResult.OutsideWorkspace;
        }

        var currentContent = GetCurrentContent(fullPath);
        if (currentContent != expectedOriginalContent)
        {
            failureDetail = $"The file now has {CountLines(currentContent)} line(s); it had "
                            + $"{CountLines(expectedOriginalContent)} when this edit was proposed - something "
                            + "changed it in between (often an earlier edit in the same turn, which shifts "
                            + "every line number below it).";
            return ApplyResult.Conflict;
        }

        if (!TryComputeResultingContent(operation, currentContent, out var newContent, out var error))
        {
            failureDetail = $"{error} The file currently has {CountLines(currentContent)} line(s).";
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

            AtomicFile.WriteAllText(fullPath, newContent);
        }

        return ApplyResult.Applied;
    }

    private DocumentViewModel? FindOpenDocument(string fullPath) =>
        _editorWorkspace.OpenDocuments.FirstOrDefault(d => string.Equals(d.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
}
