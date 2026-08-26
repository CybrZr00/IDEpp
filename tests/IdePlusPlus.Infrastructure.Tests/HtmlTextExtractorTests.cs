using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Infrastructure.Tests;

public class HtmlTextExtractorTests
{
    [Fact]
    public void ExtractReadableText_StripsScriptAndStyleContent()
    {
        var html = """
            <html><head><style>body { color: red; }</style></head>
            <body><script>alert('hi');</script><p>Real content.</p></body></html>
            """;

        var text = HtmlTextExtractor.ExtractReadableText(html);

        text.ShouldContain("Real content.");
        text.ShouldNotContain("alert");
        text.ShouldNotContain("color: red");
    }

    [Fact]
    public void ExtractReadableText_PrefersArticleOverSurroundingChrome()
    {
        var html = """
            <html><body>
            <nav>Home | About | Contact</nav>
            <article><h1>The Real Story</h1><p>This is the actual content.</p></article>
            <footer>Copyright 2026</footer>
            </body></html>
            """;

        var text = HtmlTextExtractor.ExtractReadableText(html);

        text.ShouldContain("The Real Story");
        text.ShouldContain("This is the actual content.");
        text.ShouldNotContain("Home | About | Contact");
        text.ShouldNotContain("Copyright 2026");
    }

    [Fact]
    public void ExtractReadableText_FallsBackToMainWhenNoArticle()
    {
        var html = """
            <html><body>
            <nav>Home</nav>
            <main><p>Main content here.</p></main>
            </body></html>
            """;

        var text = HtmlTextExtractor.ExtractReadableText(html);

        text.ShouldContain("Main content here.");
        text.ShouldNotContain("Home");
    }

    [Fact]
    public void ExtractReadableText_CollapsesBlankLines()
    {
        var html = "<html><body><p>Line one.</p>\n\n\n<p>Line two.</p></body></html>";

        var text = HtmlTextExtractor.ExtractReadableText(html);

        text.ShouldNotContain("\n\n");
        text.ShouldContain("Line one.");
        text.ShouldContain("Line two.");
    }

    [Fact]
    public void ExtractReadableText_DecodesHtmlEntities()
    {
        var html = "<html><body><p>Fish &amp; chips &mdash; caf&eacute;</p></body></html>";

        var text = HtmlTextExtractor.ExtractReadableText(html);

        text.ShouldContain("Fish & chips");
        text.ShouldContain("café");
    }
}
