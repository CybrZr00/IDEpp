namespace IdePlusPlus.PluginContracts;

/// <summary>
/// Snapshot handed to a plugin's editor context-menu <c>canExecute</c>/<c>execute</c> delegates
/// (see <see cref="IPluginHost.RegisterEditorContextMenuCommand"/>) describing the document and
/// caret/selection state at the moment the context menu was opened.
/// </summary>
public sealed record EditorContextMenuContext(string FilePath, string? SelectedText, int Line, int Column);

/// <summary>
/// Snapshot handed to a plugin's Solution Explorer context-menu <c>canExecute</c>/<c>execute</c>
/// delegates (see <see cref="IPluginHost.RegisterSolutionExplorerContextMenuCommand"/>) describing
/// the tree node that was right-clicked.
/// </summary>
public sealed record SolutionExplorerContextMenuContext(string Path, bool IsDirectory);
