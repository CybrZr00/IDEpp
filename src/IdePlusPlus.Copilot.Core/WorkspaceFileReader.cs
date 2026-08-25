namespace IdePlusPlus.Copilot.Core;

/// <summary>
/// Executes the read-only workspace file tools. Every path is resolved and then checked to be
/// inside the solution directory before anything is read or listed - the model chooses these paths,
/// so "../../../AppData/Local/IdePlusPlus/secrets.json" has to be impossible rather than merely
/// unlikely. Results are size-capped so one oversized file can't blow the context window.
/// </summary>
public sealed class WorkspaceFileReader
{
    public const int MaxFileChars = 60_000;
    public const int MaxListedEntries = 400;

    private static readonly string[] ExcludedDirectoryNames = ["bin", "obj", ".git", ".vs", "node_modules", ".ideplusplus"];

    private readonly Func<string, string?>? _unsavedContentProvider;

    /// <param name="unsavedContentProvider">Given a full path, returns the editor's text for a file
    /// open with unsaved changes, or null. An applied edit to an open file only touches that
    /// buffer - EditApplicationService never writes it out - so without this, a model that edits an
    /// open file and re-reads it gets its own pre-edit content back and concludes the edit did not
    /// land. Optional so the reader stays usable without an editor attached.</param>
    public WorkspaceFileReader(Func<string, string?>? unsavedContentProvider = null)
    {
        _unsavedContentProvider = unsavedContentProvider;
    }

    public string ReadFile(string? solutionDirectory, string relativePath)
    {
        if (solutionDirectory is null)
        {
            return "No solution is open, so there are no files to read.";
        }

        if (ResolveWithinRoot(solutionDirectory, relativePath) is not { } fullPath)
        {
            return $"Refused: '{relativePath}' resolves outside the open solution directory.";
        }

        if (!File.Exists(fullPath))
        {
            return $"File not found: {relativePath}";
        }

        try
        {
            var unsavedText = _unsavedContentProvider?.Invoke(fullPath);
            var text = unsavedText ?? File.ReadAllText(fullPath);

            // Said explicitly rather than left implicit: the model is about to compute an edit
            // against this text, and it needs to know which copy it is looking at.
            var unsavedNote = unsavedText is null
                ? ""
                : "\n\n[open in the editor with unsaved changes - shown above is the editor's "
                  + "content, which is what an edit will apply to. The copy on disk is older.]";

            if (text.Length <= MaxFileChars)
            {
                return WithLineNumbers(text) + unsavedNote;
            }

            // Length captured from the original text, not the truncated copy - the notice is only
            // useful if it reports how much the file actually holds.
            return WithLineNumbers(text[..MaxFileChars])
                   + $"\n\n[truncated - file is {text.Length} chars, showing first {MaxFileChars}]"
                   + unsavedNote;
        }
        catch (IOException ex)
        {
            return $"Could not read {relativePath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Prefixes every line with its 1-based number, which is what makes propose_edit's replace_range
    /// mode actually reachable: without them the model can only guess at start_line/end_line, so in
    /// practice it fell back to whole_file for every edit - re-emitting an entire file as a
    /// JSON-escaped tool argument even for a three-line change. That cost is exactly what a reasoning
    /// model weighs up and avoids by describing the edit in prose instead of calling the tool.
    /// The "N: " prefix is not part of the file; both tool descriptions say so explicitly, because a
    /// model that echoes the prefixes back inside new_text would corrupt the file it's editing.
    /// </summary>
    public static string WithLineNumbers(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return string.Join("\n", lines.Select((line, index) => $"{index + 1,6}: {line}"));
    }

    public string ListFiles(string? solutionDirectory, string? relativeDirectory, bool recursive)
    {
        if (solutionDirectory is null)
        {
            return "No solution is open, so there are no files to list.";
        }

        var target = string.IsNullOrWhiteSpace(relativeDirectory)
            ? Path.GetFullPath(solutionDirectory)
            : ResolveWithinRoot(solutionDirectory, relativeDirectory);

        if (target is null)
        {
            return $"Refused: '{relativeDirectory}' resolves outside the open solution directory.";
        }

        if (!Directory.Exists(target))
        {
            return $"Directory not found: {relativeDirectory ?? "."}";
        }

        var root = Path.GetFullPath(solutionDirectory);
        var entries = EnumerateEntries(target, recursive)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            return "(empty)";
        }

        return entries.Count > MaxListedEntries
            ? string.Join("\n", entries.Take(MaxListedEntries)) + $"\n\n[truncated - {entries.Count} entries total]"
            : string.Join("\n", entries);
    }

    private static IEnumerable<string> EnumerateEntries(string directory, bool recursive)
    {
        IEnumerable<string> directories;
        IEnumerable<string> files;
        try
        {
            directories = Directory.EnumerateDirectories(directory).Where(d => !IsExcluded(d));
            files = Directory.EnumerateFiles(directory);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
        }

        foreach (var subdirectory in directories)
        {
            yield return subdirectory;

            if (!recursive)
            {
                continue;
            }

            foreach (var nested in EnumerateEntries(subdirectory, recursive: true))
            {
                yield return nested;
            }
        }
    }

    private static bool IsExcluded(string directoryPath) =>
        ExcludedDirectoryNames.Contains(Path.GetFileName(directoryPath), StringComparer.OrdinalIgnoreCase);

    /// <summary>Full path if it stays inside root, otherwise null. Compared with a trailing separator
    /// so a sibling directory sharing a name prefix (e.g. "MySolution.Backup") can't pass as inside
    /// "MySolution".</summary>
    public static string? ResolveWithinRoot(string root, string relativePath)
    {
        string fullPath;
        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(root);
            fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }
}
