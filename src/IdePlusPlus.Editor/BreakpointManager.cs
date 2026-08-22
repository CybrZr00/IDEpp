namespace IdePlusPlus.Editor;

public sealed class BreakpointManager
{
    private readonly Dictionary<string, HashSet<int>> _linesByFile = new(StringComparer.OrdinalIgnoreCase);

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

        Changed?.Invoke();
    }

    public IReadOnlyCollection<int> GetLines(string filePath) =>
        _linesByFile.TryGetValue(filePath, out var lines) ? lines : Array.Empty<int>();
}
