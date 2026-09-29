using System.Text;
using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class NamespaceCorrectorTests
{
    /// <summary>Exactly what `dotnet new class` writes, captured from the 10.0.301 SDK.</summary>
    private const string GeneratedClass = """
        namespace Demo;

        public class Widget
        {

        }

        """;

    [Fact]
    public void RewritesAFileScopedNamespace() =>
        NamespaceCorrector.Replace(GeneratedClass, "Demo", "Demo.Models")
            .ShouldStartWith("namespace Demo.Models;");

    [Fact]
    public void RewritesABlockScopedNamespace() =>
        NamespaceCorrector.Replace("namespace Demo\n{\n}\n", "Demo", "Demo.Controllers")
            .ShouldBe("namespace Demo.Controllers\n{\n}\n");

    [Fact]
    public void KeepsTheIndentation() =>
        NamespaceCorrector.Replace("    namespace Demo;\n", "Demo", "Demo.Models")
            .ShouldBe("    namespace Demo.Models;\n");

    /// <summary>The reason this is anchored and lookahead-guarded rather than a plain string
    /// replace: the root namespace shows up in using directives and type references too, and
    /// rewriting those would break the file this is supposed to be fixing.</summary>
    [Fact]
    public void LeavesUsingDirectivesAndTypeReferencesAlone()
    {
        const string Source = """
            using Demo;
            using Demo.Abstractions;

            namespace Demo;

            public class Widget : Demo.WidgetBase
            {
                // Demo
            }
            """;

        var corrected = NamespaceCorrector.Replace(Source, "Demo", "Demo.Models");

        corrected.ShouldContain("using Demo;");
        corrected.ShouldContain("using Demo.Abstractions;");
        corrected.ShouldContain("namespace Demo.Models;");
        corrected.ShouldContain(": Demo.WidgetBase");
        corrected.ShouldContain("// Demo");
    }

    /// <summary>A longer namespace that merely starts with the bound one is a different namespace.</summary>
    [Fact]
    public void DoesNotMatchANamespaceThatOnlySharesAPrefix() =>
        NamespaceCorrector.Replace("namespace DemoOther;\n", "Demo", "Demo.Models")
            .ShouldBe("namespace DemoOther;\n");

    [Fact]
    public void LeavesAFileWhoseNamespaceIsAlreadySomethingElseAlone() =>
        NamespaceCorrector.Replace("namespace Other.Thing;\n", "Demo", "Demo.Models")
            .ShouldBe("namespace Other.Thing;\n");

    [Fact]
    public void CorrectsARealFileOnDisk()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteFile("Widget.cs", GeneratedClass);

        NamespaceCorrector.TryCorrect(path, "Demo", "Demo.Models").ShouldBeTrue();
        File.ReadAllText(path).ShouldStartWith("namespace Demo.Models;");
    }

    /// <summary>`dotnet new` writes a UTF-8 BOM. Silently dropping it would change the encoding of a
    /// file this only meant to fix one line of.</summary>
    [Fact]
    public void PreservesAUtf8Bom()
    {
        using var directory = new TempDirectory();
        var path = directory.PathFor("Widget.cs");
        File.WriteAllBytes(path, new UTF8Encoding(true).GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(GeneratedClass)).ToArray());

        NamespaceCorrector.TryCorrect(path, "Demo", "Demo.Models").ShouldBeTrue();

        var bytes = File.ReadAllBytes(path);
        bytes[..3].ShouldBe([0xEF, 0xBB, 0xBF]);
        File.ReadAllText(path).ShouldStartWith("namespace Demo.Models;");
    }

    [Fact]
    public void DoesNotAddABomToAFileThatHadNone()
    {
        using var directory = new TempDirectory();
        var path = directory.PathFor("Widget.cs");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(GeneratedClass));

        NamespaceCorrector.TryCorrect(path, "Demo", "Demo.Models").ShouldBeTrue();

        File.ReadAllBytes(path)[0].ShouldNotBe((byte)0xEF);
    }

    [Fact]
    public void DoesNothingWhenTheNamespaceIsAlreadyRight()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteFile("Widget.cs", GeneratedClass);

        NamespaceCorrector.TryCorrect(path, "Demo", "Demo").ShouldBeFalse();
        File.ReadAllText(path).ShouldBe(GeneratedClass);
    }

    /// <summary>Razor, XAML and config files are left to whatever the template wrote.</summary>
    [Fact]
    public void IgnoresFilesThatArentCSharp()
    {
        using var directory = new TempDirectory();
        var path = directory.WriteFile("Page.razor", "@namespace Demo\n");

        NamespaceCorrector.TryCorrect(path, "Demo", "Demo.Pages").ShouldBeFalse();
    }

    [Fact]
    public void AMissingFileIsNotAFailure() =>
        NamespaceCorrector.TryCorrect(
            Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid()}.cs"), "Demo", "Demo.Models")
            .ShouldBeFalse();
}
