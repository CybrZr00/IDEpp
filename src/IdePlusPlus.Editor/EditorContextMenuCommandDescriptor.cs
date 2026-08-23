namespace IdePlusPlus.Editor;

/// <summary>
/// Snapshot of the document/caret state at the moment the editor's right-click context menu was
/// opened. Deliberately a plain record defined in IdePlusPlus.Editor (not
/// IdePlusPlus.PluginContracts.EditorContextMenuContext, which has the same shape) so this project
/// doesn't need a reference to PluginContracts - see <see cref="EditorContextMenuCommandDescriptor"/>
/// and EditorServices.EditorContextMenuCommands' remarks for the full rationale (the same "ambient
/// static locator + narrow delegate" pattern CompletionProvider already uses). The Shell project,
/// which does reference PluginContracts, converts between the two shapes when it wires
/// EditorServices.EditorContextMenuCommands up in App.xaml.cs.
/// </summary>
public sealed record EditorContextMenuState(string FilePath, string? SelectedText, int Line, int Column);

/// <summary>
/// One command to offer in the editor's right-click context menu, evaluated fresh each time the
/// menu is about to open. <see cref="CanExecute"/> decides whether the item is shown at all for the
/// current <see cref="EditorContextMenuState"/>; <see cref="Execute"/> runs when it's clicked.
/// </summary>
public sealed record EditorContextMenuCommandDescriptor(
    string Header,
    Func<EditorContextMenuState, bool> CanExecute,
    Action<EditorContextMenuState> Execute);
