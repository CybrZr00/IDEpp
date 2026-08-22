using System.Windows.Threading;

namespace IdePlusPlus.Editor;

/// <summary>
/// The file/line the debugger is currently stopped at, if any. DebugSession updates this from a
/// background thread (the DAP reader loop), so updates are marshalled to the UI thread here rather
/// than requiring every caller to do it.
/// </summary>
public sealed class DebugLocationIndicator
{
    private readonly Dispatcher _dispatcher;

    public DebugLocationIndicator()
    {
        _dispatcher = System.Windows.Application.Current!.Dispatcher;
    }

    public event Action? Changed;

    public string? FilePath { get; private set; }

    public int? Line { get; private set; }

    public void Show(string filePath, int line) => _dispatcher.Invoke(() =>
    {
        FilePath = filePath;
        Line = line;
        Changed?.Invoke();
    });

    public void Clear() => _dispatcher.Invoke(() =>
    {
        FilePath = null;
        Line = null;
        Changed?.Invoke();
    });
}
