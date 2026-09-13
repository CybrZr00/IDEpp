using System.IO;
using System.Xml;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Shouldly;
using Xunit;

namespace AccessVbaPlugin.Tests;

/// <summary>
/// Runs Vba.xshd through AvalonEdit's real highlighting engine over representative code, headlessly.
/// Loading the definition alone proves nothing - a rule that compiles to an empty regex (e.g. an
/// unescaped "#", which IgnorePatternWhitespace treats as a comment) only fails when a line is
/// actually highlighted, which is what took the tool window down the first time it showed a module.
/// </summary>
public sealed class VbaHighlightingTests
{
    private const string Sample = """
        Option Compare Database
        ' a comment with "quotes" and #hash# inside
        #If Win64 Then
        Private Declare PtrSafe Function GetTickCount Lib "kernel32" () As Long
        #End If
        Dim s As String, d As Date
        s = "He said ""hi"" to me"
        d = #12/31/2024#
        If Nz(s, vbNullString) <> "" Then MsgBox s, vbOKOnly
        Attribute VB_Name = "Sample"
        """;

    private static IHighlightingDefinition LoadDefinition()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AccessVbaPlugin", "Vba.xshd")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull("could not locate samples/AccessVbaPlugin/Vba.xshd above " + AppContext.BaseDirectory);
        using var reader = XmlReader.Create(Path.Combine(directory.FullName, "AccessVbaPlugin", "Vba.xshd"));
        return HighlightingLoader.Load(reader, HighlightingManager.Instance);
    }

    private static List<HighlightedLine> HighlightAll(string text)
    {
        var document = new TextDocument(text);
        var highlighter = new DocumentHighlighter(document, LoadDefinition());
        return Enumerable.Range(1, document.LineCount).Select(highlighter.HighlightLine).ToList();
    }

    private static string? ColorAt(HighlightedLine line, string word)
    {
        var text = line.Document.GetText(line.DocumentLine);
        var index = text.IndexOf(word, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, word);
        var offset = line.DocumentLine.Offset + index;
        return line.Sections.FirstOrDefault(s => offset >= s.Offset && offset < s.Offset + s.Length)?.Color.Name;
    }

    [Fact]
    public void EveryLineHighlightsWithoutThrowing()
    {
        Should.NotThrow(() => HighlightAll(Sample));
    }

    [Fact]
    public void ColorsTheConstructsVbaIsPeculiarAbout()
    {
        var lines = HighlightAll(Sample);

        ColorAt(lines[1], "' a comment").ShouldBe("Comment");
        ColorAt(lines[1], "#hash#").ShouldBe("Comment");
        ColorAt(lines[2], "#If").ShouldBe("Preprocessor");
        ColorAt(lines[3], "Declare").ShouldBe("Keyword");
        ColorAt(lines[3], "\"kernel32\"").ShouldBe("String");
        ColorAt(lines[5], "Dim").ShouldBe("Keyword");
        ColorAt(lines[5], "String").ShouldBe("DataType");
        ColorAt(lines[7], "#12/31/2024#").ShouldBe("DateLiteral");
        ColorAt(lines[8], "Nz").ShouldBe("BuiltinFunction");
        ColorAt(lines[8], "vbNullString").ShouldBe("Constant");
        ColorAt(lines[8], "vbOKOnly").ShouldBe("Constant");
        ColorAt(lines[9], "Attribute VB_Name").ShouldBe("Attribute");
    }

    [Fact]
    public void ADoubledQuoteDoesNotEndAStringLiteral()
    {
        var line = HighlightAll(Sample)[6];
        var text = line.Document.GetText(line.DocumentLine);
        var literal = "\"He said \"\"hi\"\" to me\"";
        var start = line.DocumentLine.Offset + text.IndexOf(literal, StringComparison.Ordinal);

        var section = line.Sections.Single(s => s.Color.Name == "String");
        section.Offset.ShouldBe(start);
        section.Length.ShouldBe(literal.Length);
    }
}
