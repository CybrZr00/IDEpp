using System.IO;
using System.Text.Json;

namespace IdePlusPlus.Editor;

/// <summary>
/// Breakpoints persist to %LOCALAPPDATA%\IdePlusPlus\breakpoints.json (same JSON-MRU-file
/// convention EditorWorkspace already uses for recent-files.json) so they survive an IDE restart
/// instead of resetting to empty every launch.
/// </summary>
public sealed class BreakpointManager
{
    private static readonly string DefaultBreakpointsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IdePlusPlus", "breakpoints.json");

    private readonly string _breakpointsPath;
    private readonly Dictionary<string, HashSet<int>> _linesByFile;

    public BreakpointManager(string? breakpointsPath = null)
    {
        _breakpointsPath = breakpointsPath ?? DefaultBreakpointsPath;
        _linesByFile = Load(_breakpointsPath);
    }

    public event Action? Changed;

    public IReadOnlyDictionary<string, HashSet<int>> AllBreakpoints => _linesByFile;

    public bool IsBreakpoint(string filePath, int line) =>
        _linesByFile.TryGetValue(filePath, out var lines) && lines.Contains(line);

    public void Toggle(string filePath, int line)
    {
        if (!_linesByFile.TryGetValue(filePath, out var lines))
        {
            lines = [];
            _linesByFile[filePath] = lines;
        }

        if (!lines.Remove(line))
        {
            lines.Add(line);
        }

        Save();
        Changed?.Invoke();
    }

    public IReadOnlyCollection<int> GetLines(string filePath) =>
        _linesByFile.TryGetValue(filePath, out var lines) ? lines : Array.Empty<int>();

    private static Dictionary<string, HashSet<int>> Load(string path)
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(path))
            {
                return result;
            }

            var raw = JsonSerializer.Deserialize<Dictionary<string, List<int>>>(File.ReadAllText(path));
            if (raw is null)
            {
                return result;
            }

            foreach (var (file, lines) in raw)
            {
                result[file] = [.. lines];
            }

            return result;
        }
        catch (Exception)
        {
            // Corrupt/unreadable file from a previous version - start with no breakpoints.
            return new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_breakpointsPath)!;
        Directory.CreateDirectory(directory);

        var raw = _linesByFile.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        File.WriteAllText(_breakpointsPath, JsonSerializer.Serialize(raw));
    }
}
