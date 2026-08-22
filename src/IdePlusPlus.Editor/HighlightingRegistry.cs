using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace IdePlusPlus.Editor;

/// <summary>
/// Maps file extensions to one of AvalonEdit's built-in highlighting definitions. AvalonEdit ships
/// many more definitions as embedded resources than it registers extensions for (C#, XML, Json,
/// MarkDown, JavaScript, HTML, CSS, C++, Java, PHP, Python, PowerShell, VB, TSQL, ...), so this only
/// needs to own the extension-to-definition-name mapping - no custom .xshd authoring required.
/// Plugins can also layer their own extension mappings on top via RegisterCustom (see PluginHost).
/// </summary>
public static class HighlightingRegistry
{
    private static readonly Dictionary<string, IHighlightingDefinition> CustomByExtension = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Loads a .xshd file and registers it for an extension, taking priority over any built-in mapping.</summary>
    public static void RegisterCustom(string fileExtension, string xshdFilePath)
    {
        using var reader = XmlReader.Create(xshdFilePath);
        var definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        CustomByExtension[NormalizeExtension(fileExtension)] = definition;
    }

    public static IHighlightingDefinition? GetHighlightingFor(string? filePath)
    {
        if (filePath is null)
        {
            return null;
        }

        var extension = Path.GetExtension(filePath);

        if (CustomByExtension.TryGetValue(extension, out var custom))
        {
            return custom;
        }

        return DefinitionNameByExtension.TryGetValue(extension, out var name)
            ? HighlightingManager.Instance.GetDefinition(name)
            : null;
    }

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;
}
