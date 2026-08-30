using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class TemplateCreationArgumentsTests
{
    private static TemplateCreationRequest Request(
        string shortName = "classlib",
        string name = "MyLibrary",
        string outputDirectory = @"C:\repo\MyLibrary") =>
        new(shortName, name, outputDirectory);

    [Fact]
    public void BuildsTheMinimalCreateCommand() =>
        TemplateCreationArguments.ForCreate(Request()).ShouldBe(
        [
            "new", "classlib",
            "--name", "MyLibrary",
            "--output", @"C:\repo\MyLibrary",
            "--no-update-check",
        ]);

    /// <summary>
    /// The regression test for the trap that would put every generated file in the wrong directory.
    /// Measured on the 10.0.301 SDK: `dotnet new create class -n X -o Sub` writes X.cs to the working
    /// directory, not Sub - in both argument positions - and still prints "created successfully" and
    /// exits 0. Only the implicit form honors --output, so the template's short name must follow
    /// "new" directly with no verb between them.
    /// </summary>
    [Fact]
    public void UsesTheImplicitFormBecauseCreateSilentlyIgnoresOutput()
    {
        var arguments = TemplateCreationArguments.ForCreate(Request());

        arguments[0].ShouldBe("new");
        arguments[1].ShouldBe("classlib");
        arguments.ShouldNotContain("create");
    }

    [Fact]
    public void PairsTheFrameworkWithItsValue()
    {
        var arguments = TemplateCreationArguments.ForCreate(Request() with { Framework = "net8.0" });

        arguments[arguments.ToList().IndexOf("--framework") + 1].ShouldBe("net8.0");
    }

    [Fact]
    public void PassesTheLanguageWhenOneIsChosen()
    {
        var arguments = TemplateCreationArguments.ForCreate(Request("class") with { Language = "VB" });

        arguments[arguments.ToList().IndexOf("--language") + 1].ShouldBe("VB");
    }

    /// <summary>Only the templates that declare the parameter get it; the caller checks
    /// DeclaresNamespaceParameter, because passing it to one that doesn't is a hard CLI error.</summary>
    [Fact]
    public void PassesAnExplicitNamespace()
    {
        var arguments = TemplateCreationArguments.ForCreate(
            Request("apicontroller") with { Namespace = "Demo.Controllers" });

        arguments[arguments.ToList().IndexOf("--namespace") + 1].ShouldBe("Demo.Controllers");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OmitsOptionalValuesThatWereNeverSet(string? value)
    {
        var arguments = TemplateCreationArguments.ForCreate(
            Request() with { Framework = value, Language = value, Namespace = value });

        arguments.ShouldNotContain("--framework");
        arguments.ShouldNotContain("--language");
        arguments.ShouldNotContain("--namespace");
    }

    [Fact]
    public void PassesTemplateParametersWithTheirValues()
    {
        var arguments = TemplateCreationArguments.ForCreate(Request() with
        {
            Parameters = new Dictionary<string, string?> { ["langVersion"] = "preview" },
        });

        arguments.TakeLast(2).ShouldBe(["--langVersion", "preview"]);
    }

    /// <summary>A bool parameter is a switch: `--actions`, not `--actions true`.</summary>
    [Fact]
    public void PassesANullValuedParameterAsASwitch()
    {
        var arguments = TemplateCreationArguments.ForCreate(Request("apicontroller") with
        {
            Parameters = new Dictionary<string, string?> { ["actions"] = null },
        });

        arguments.Last().ShouldBe("--actions");
    }

    /// <summary>The implicit form's one cost: a template short name that is also a sub-command would
    /// silently run the sub-command. None of the 228 stock templates collides, but a third-party
    /// package could, and failing loudly beats running `dotnet new list` and reporting success.</summary>
    [Theory]
    [InlineData("list")]
    [InlineData("install")]
    [InlineData("search")]
    [InlineData("create")]
    public void RefusesAShortNameThatIsADotnetNewSubCommand(string shortName) =>
        Should.Throw<ArgumentException>(() => TemplateCreationArguments.ForCreate(Request(shortName)));

    [Fact]
    public void BuildsTheSolutionAddCommandForASolutionFolder() =>
        TemplateCreationArguments.ForSolutionAdd(@"C:\repo\App.slnx", @"C:\repo\src\Lib\Lib.csproj", "src")
            .ShouldBe(["solution", @"C:\repo\App.slnx", "add", @"C:\repo\src\Lib\Lib.csproj", "--solution-folder", "src"]);

    /// <summary>Left unsaid, the CLI derives a solution folder from the project's path on disk - so
    /// "no folder" has to be said explicitly rather than by omission.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildsTheSolutionAddCommandForTheSolutionRoot(string? solutionFolder) =>
        TemplateCreationArguments.ForSolutionAdd(@"C:\repo\App.slnx", @"C:\repo\Lib\Lib.csproj", solutionFolder)
            .ShouldBe(["solution", @"C:\repo\App.slnx", "add", @"C:\repo\Lib\Lib.csproj", "--in-root"]);
}
