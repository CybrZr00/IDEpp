namespace IdePlusPlus.Editor;

/// <summary>
/// Describes where in the document the mouse came to rest, for a hover-provider lookup. A plain
/// record defined here (not IdePlusPlus.PluginContracts.PluginHoverContext, which has the same
/// shape) for the same "keep IdePlusPlus.Editor free of a PluginContracts reference" reason as
/// <see cref="EditorContextMenuState"/> - see EditorServices.HoverProviderLookup's remarks.
/// </summary>
public sealed record EditorHoverRequest(string FilePath, int Line, int Column, string Word);
