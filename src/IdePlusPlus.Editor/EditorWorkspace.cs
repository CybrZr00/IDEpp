using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace IdePlusPlus.Editor;

public sealed class EditorWorkspace
{
    private const int MaxRecentFiles = 10;

    private static readonly string DefaultRecentFilesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus",
        "recent-files.json");

    private readonly string _recentFilesPath;
    private int _untitledCount;

    public EditorWorkspace(string? recentFilesPath = null)
    {
        _recentFilesPath = recentFilesPath ?? DefaultRecentFilesPath;
        RecentFiles = new ObservableCollection<string>(LoadRecentFiles(_recentFilesPath));
    }

    public ObservableCollection<DocumentViewModel> OpenDocuments { get; } = [];

    public ObservableCollection<string> RecentFiles { get; }

    public DocumentViewModel NewDocument()
    {
        _untitledCount++;
        var document = DocumentViewModel.ForNewFile($"Untitled{_untitledCount}");
        OpenDocuments.Add(document);
        return document;
    }

    /// <summary>
    /// Opens filePath, or returns null if it can no longer be read (deleted/renamed/unavailable
    /// drive since it was added to Recent Files) - pruning the stale entry rather than letting the
    /// caller's File.ReadAllText crash out as an unhandled exception.
    /// </summary>
    public DocumentViewModel? Open(string filePath)
    {
        var existing = OpenDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            TouchRecentFile(filePath);
            return existing;
        }

        if (!File.Exists(filePath))
        {
            RemoveFromRecentFiles(filePath);
            return null;
        }

        DocumentViewModel document;
        try
        {
            document = DocumentViewModel.ForExistingFile(filePath);
        }
        catch (IOException)
        {
            RemoveFromRecentFiles(filePath);
            return null;
        }

        OpenDocuments.Add(document);
        TouchRecentFile(filePath);
        return document;
    }

    /// <summary>
    /// Like Open, but a target that doesn't exist on disk yet is not an error here - it's the
    /// normal case for reviewing a propose_edit that creates a new file. Opens an empty,
    /// not-yet-saved document bound to that path instead of failing, so there's a real editor tab
    /// for the diff review panel to attach to before the file exists.
    /// </summary>
    public DocumentViewModel? OpenOrCreate(string filePath)
    {
        if (File.Exists(filePath))
        {
            return Open(filePath);
        }

        var existing = OpenDocuments.FirstOrDefault(d =>
            string.Equals(d.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var document = DocumentViewModel.ForNewFile(Path.GetFileName(filePath));
        document.AttachToFile(filePath);
        OpenDocuments.Add(document);
        return document;
    }

    private void RemoveFromRecentFiles(string filePath)
    {
        if (RecentFiles.Remove(filePath))
        {
            SaveRecentFiles();
        }
    }

    public void Save(DocumentViewModel document)
    {
        if (document.FilePath is null)
        {
            throw new InvalidOperationException($"{nameof(Save)} requires a file path; call {nameof(SaveAs)} for a new document.");
        }

        // The directory may not exist yet - e.g. OpenOrCreate's document for a propose_edit that
        // creates a file in a new subfolder, or a manual Save As into one. EditApplicationService.
        // Apply's direct-disk-write branch already creates it for the same reason; this is that
        // same guard for every other route to a first write, so an unhandled DirectoryNotFoundException
        // doesn't take the save down.
        var directory = Path.GetDirectoryName(document.FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(document.FilePath, document.TextDocument.Text);
        document.IsDirty = false;
        TouchRecentFile(document.FilePath);
    }

    public void SaveAs(DocumentViewModel document, string filePath)
    {
        document.AttachToFile(filePath);
        Save(document);
    }

    public void Close(DocumentViewModel document)
    {
        OpenDocuments.Remove(document);
    }

    private void TouchRecentFile(string filePath)
    {
        RecentFiles.Remove(filePath);
        RecentFiles.Insert(0, filePath);
        while (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveAt(RecentFiles.Count - 1);
        }

        SaveRecentFiles();
    }

    private static List<string> LoadRecentFiles(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception)
        {
            // Corrupt/unreadable MRU file from a previous version — start with an empty list.
            return [];
        }
    }

    private void SaveRecentFiles()
    {
        var directory = Path.GetDirectoryName(_recentFilesPath)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(_recentFilesPath, JsonSerializer.Serialize(RecentFiles));
    }
}
