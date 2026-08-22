using System.IO;
using ICSharpCode.AvalonEdit.Highlighting;

namespace IdePlusPlus.Editor;

/// <summary>
/// Maps file extensions to one of AvalonEdit's built-in highlighting definitions. AvalonEdit ships
/// many more definitions as embedded resources than it registers extensions for (C#, XML, Json,
/// MarkDown, JavaScript, HTML, CSS, C++, Java, PHP, Python, PowerShell, VB, TSQL, ...), so this only
/// needs to own the extension-to-definition-name mapping - no custom .xshd authoring required.
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
        [".js"] = "JavaScript",
        [".mjs"] = "JavaScript",
        [".cjs"] = "JavaScript",
        [".ts"] = "JavaScript",
        [".tsx"] = "JavaScript",
        [".jsx"] = "JavaScript",
        [".html"] = "HTML",
        [".htm"] = "HTML",
        [".css"] = "CSS",
        [".c"] = "C++",
        [".h"] = "C++",
        [".cpp"] = "C++",
        [".cc"] = "C++",
        [".cxx"] = "C++",
        [".hpp"] = "C++",
        [".java"] = "Java",
        [".php"] = "PHP",
        [".py"] = "Python",
        [".ps1"] = "PowerShell",
        [".psm1"] = "PowerShell",
        [".psd1"] = "PowerShell",
        [".vb"] = "VB",
        [".sql"] = "TSQL",
        [".tex"] = "TeX",
        [".patch"] = "Patch",
        [".diff"] = "Patch",
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
