using IdePlusPlus.Build;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Build.Tests;

public class BuildDiagnosticParserTests
{
    [Fact]
    public void TryParse_ErrorLineWithProjectSuffix_ParsesAllFields()
    {
        var diagnostic = BuildDiagnosticParser.TryParse(
            @"C:\repo\src\Foo.cs(12,5): error CS0246: The type or namespace name 'Bar' could not be found [C:\repo\src\Foo.csproj]");

        diagnostic.ShouldNotBeNull();
        diagnostic.Severity.ShouldBe(BuildDiagnosticSeverity.Error);
        diagnostic.FilePath.ShouldBe(@"C:\repo\src\Foo.cs");
        diagnostic.Line.ShouldBe(12);
        diagnostic.Column.ShouldBe(5);
        diagnostic.Code.ShouldBe("CS0246");
        diagnostic.Message.ShouldBe("The type or namespace name 'Bar' could not be found");
    }

    [Fact]
    public void TryParse_WarningLine_ParsesAsWarning()
    {
        var diagnostic = BuildDiagnosticParser.TryParse(
            @"Foo.cs(3,1): warning CS0168: The variable 'x' is declared but never used [Foo.csproj]");

        diagnostic.ShouldNotBeNull();
        diagnostic.Severity.ShouldBe(BuildDiagnosticSeverity.Warning);
        diagnostic.Code.ShouldBe("CS0168");
    }

    [Fact]
    public void TryParse_LineWithoutProjectSuffix_StillParses()
    {
        var diagnostic = BuildDiagnosticParser.TryParse("Foo.cs(1,1): error CS1002: ; expected");

        diagnostic.ShouldNotBeNull();
        diagnostic.Message.ShouldBe("; expected");
    }

    [Fact]
    public void TryParse_PlainOutputLine_ReturnsNull()
    {
        BuildDiagnosticParser.TryParse("Build started: solution.slnx").ShouldBeNull();
    }

    [Fact]
    public void TryParse_EmptyLine_ReturnsNull()
    {
        BuildDiagnosticParser.TryParse("").ShouldBeNull();
    }

    [Fact]
    public void TryParse_SuccessSummaryLine_ReturnsNull()
    {
        BuildDiagnosticParser.TryParse("Build succeeded.").ShouldBeNull();
    }
}
