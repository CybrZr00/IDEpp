using System.Linq;
using IdePlusPlus.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// Exercises <see cref="LiveDiagnosticMapper.ToLiveDiagnostic"/> against real <see cref="Diagnostic"/>
/// instances obtained from plain <see cref="CSharpSyntaxTree"/> parsing - no MSBuildWorkspace, no
/// MSBuildLocator registration, and no disk I/O needed, since this is pure line/column/field mapping
/// logic that doesn't depend on project loading at all.
/// </summary>
public sealed class LiveDiagnosticMapperTests
{
    [Fact]
    public void ToLiveDiagnostic_ConvertsZeroBasedRoslynPositionsToOneBasedLineAndColumn()
    {
        // Line 0 (blank), line 1 has a missing semicolon after "1" at column 24 (0-based).
        const string source = "\nclass C { void M() { int x = 1 } }\n";
        var tree = CSharpSyntaxTree.ParseText(source);
        var diagnostic = tree.GetDiagnostics().Single(d => d.Id == "CS1002");

        var result = LiveDiagnosticMapper.ToLiveDiagnostic(diagnostic, "Fake.cs");

        var expectedSpan = diagnostic.Location.GetLineSpan();
        result.FilePath.ShouldBe("Fake.cs");
        result.Line.ShouldBe(expectedSpan.StartLinePosition.Line + 1);
        result.Column.ShouldBe(expectedSpan.StartLinePosition.Character + 1);
        result.EndLine.ShouldBe(expectedSpan.EndLinePosition.Line + 1);
        result.EndColumn.ShouldBe(expectedSpan.EndLinePosition.Character + 1);
        // Sanity: Roslyn's own span is 0-based, so the mapped result must be strictly greater.
        result.Line.ShouldBeGreaterThan(expectedSpan.StartLinePosition.Line);
        result.Column.ShouldBeGreaterThan(expectedSpan.StartLinePosition.Character);
    }

    [Fact]
    public void ToLiveDiagnostic_CopiesSeverityCodeAndMessageVerbatim()
    {
        const string source = "class C { void M() { int x = 1 } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var diagnostic = tree.GetDiagnostics().Single(d => d.Id == "CS1002");

        var result = LiveDiagnosticMapper.ToLiveDiagnostic(diagnostic, "Whatever.cs");

        result.Code.ShouldBe("CS1002");
        result.Severity.ShouldBe(diagnostic.Severity.ToString());
        result.Severity.ShouldBe("Error");
        result.Message.ShouldBe(diagnostic.GetMessage());
    }

    [Fact]
    public void ToLiveDiagnostic_UsesTheSuppliedFilePathRatherThanAnyPathOnTheDiagnosticItself()
    {
        // The syntax tree here has no associated file path at all - ToLiveDiagnostic must take
        // FilePath purely from its parameter, mirroring how RoslynWorkspaceService always passes
        // the caller-supplied document path rather than trusting Diagnostic.Location.
        const string source = "class C { void M() { int x = 1 } }";
        var tree = CSharpSyntaxTree.ParseText(source);
        var diagnostic = tree.GetDiagnostics().Single(d => d.Id == "CS1002");

        var result = LiveDiagnosticMapper.ToLiveDiagnostic(diagnostic, "C:\\some\\path\\File.cs");

        result.FilePath.ShouldBe("C:\\some\\path\\File.cs");
    }
}
