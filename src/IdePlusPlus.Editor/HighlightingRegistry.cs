using System.IO;
using ICSharpCode.AvalonEdit.Highlighting;

namespace IdePlusPlus.Editor;

/// <summary>
/// Maps file extensions to one of AvalonEdit's built-in highlighting definitions.
/// AvalonEdit ships C#/XML/Json/MarkDown definitions as embedded resources already,
/// so this only needs to own the extension-to-definition-name mapping.
/// </summary>
public static class HighlightingRegistry
{
    private static readonly Dictionary<string, string> DefinitionNameByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "C#",
        [".xaml"] = "XML",
        [".xml"] = "XML",
        [".csproj"] = "XML",
        [".json"] = "Json",
        [".md"] = "MarkDown",
    };

    public static IHighlightingDefinition? GetHighlightingFor(string? filePath)
    {
        if (filePath is null)
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);
        return DefinitionNameByExtension.TryGetValue(extension, out var name)
            ? HighlightingManager.Instance.GetDefinition(name)
            : null;
    }
}
