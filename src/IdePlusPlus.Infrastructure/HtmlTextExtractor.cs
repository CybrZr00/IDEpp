using HtmlAgilityPack;

namespace IdePlusPlus.Infrastructure;

/// <summary>
/// Reduces a fetched HTML page to the text a model actually needs to read - script/style contents
/// stripped (they are not prose, and a script body can itself look like instructions), the main
/// article body preferred over surrounding nav/footer chrome when the page marks one, and runs of
/// blank lines collapsed so boilerplate-heavy pages don't burn the tool's output budget on
/// whitespace.
/// </summary>
public static class HtmlTextExtractor
{
    public static string ExtractReadableText(string html)
    {
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var unwanted = document.DocumentNode.SelectNodes("//script|//style|//noscript");
        if (unwanted is not null)
        {
            foreach (var node in unwanted)
            {
                node.Remove();
            }
        }

        var content =
            document.DocumentNode.SelectSingleNode("//article")
            ?? document.DocumentNode.SelectSingleNode("//main")
            ?? document.DocumentNode;

        var text = HtmlEntity.DeEntitize(content.InnerText);

        var lines = text
            .Replace("\r\n", "\n")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);

        return string.Join("\n", lines);
    }
}
