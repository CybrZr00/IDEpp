namespace IdePlusPlus.Editor;

/// <summary>
/// The caret line/column of whichever editor currently has keyboard focus, for the shell's status
/// bar. TextEditorHost instances push updates here directly (all on the UI thread already), similar
/// to how BreakpointMargin reads BreakpointManager.
/// </summary>
public sealed class CaretPositionIndicator
{
    public event Action? Changed;

    public int Line { get; private set; } = 1;

    public int Column { get; private set; } = 1;

    public void Update(int line, int column)
    {
        Line = line;
        Column = column;
        Changed?.Invoke();
    }
}
