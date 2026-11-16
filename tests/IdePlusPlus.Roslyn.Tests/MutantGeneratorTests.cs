using IdePlusPlus.Roslyn.Mutation;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// One test per mutator the generator offers, plus the line-span filter that keeps it to what a merge
/// changed. These are pure syntax-tree tests - no MSBuild, no workspace - so they run in milliseconds.
/// </summary>
public class MutantGeneratorTests
{
    [Fact]
    public void Generate_FlipsRelationalOperatorsBothWays()
    {
        var lessThan = MutantGenerator.Generate("class C { bool M(int a, int b) => a < b; }");
        lessThan.ShouldContain(m => m.Kind == "relational" && m.Description.Contains("'<' to '>'") && m.Replacement == ">");

        var greaterThan = MutantGenerator.Generate("class C { bool M(int a, int b) => a > b; }");
        greaterThan.ShouldContain(m => m.Kind == "relational" && m.Replacement == "<");
    }

    [Fact]
    public void Generate_ShiftsBoundariesByOne()
    {
        var mutant = MutantGenerator.Generate("class C { bool M(int a, int b) => a <= b; }")
            .First(m => m.Kind == "boundary");
        mutant.Replacement.ShouldBe("<");

        MutantGenerator.Generate("class C { bool M(int a, int b) => a > b; }")
            .ShouldContain(m => m.Kind == "boundary" && m.Replacement == ">=");
    }

    [Fact]
    public void Generate_FlipsEqualityBothWays()
    {
        MutantGenerator.Generate("class C { bool M(int a, int b) => a == b; }")
            .ShouldContain(m => m.Kind == "equality" && m.Replacement == "!=");

        MutantGenerator.Generate("class C { bool M(int a, int b) => a != b; }")
            .ShouldContain(m => m.Kind == "equality" && m.Replacement == "==");
    }

    [Fact]
    public void Generate_SwapsAndAndOr()
    {
        var and = MutantGenerator.Generate("class C { bool M(bool a, bool b) => a && b; }")
            .First(m => m.Kind == "logical");
        and.Replacement.ShouldBe("||");
        and.ApplyTo("class C { bool M(bool a, bool b) => a && b; }")
            .ShouldBe("class C { bool M(bool a, bool b) => a || b; }");

        MutantGenerator.Generate("class C { bool M(bool a, bool b) => a || b; }")
            .ShouldContain(m => m.Kind == "logical" && m.Replacement == "&&");
    }

    [Fact]
    public void Generate_NegatesAnIfConditionButNotOneAlreadyNegated()
    {
        const string source = "class C { void M(bool ready) { if (ready) { Go(); } } void Go() { } }";
        var mutant = MutantGenerator.Generate(source).First(m => m.Kind == "negation");
        mutant.Replacement.ShouldBe("!(ready)");
        mutant.ApplyTo(source).ShouldContain("if (!(ready))");

        MutantGenerator.Generate("class C { void M(bool ready) { if (!ready) { Go(); } } void Go() { } }")
            .ShouldNotContain(m => m.Kind == "negation");
    }

    [Fact]
    public void Generate_ReplacesAReturnedExpressionWithDefault()
    {
        const string source = "class C { int M() { return 42; } }";
        var mutant = MutantGenerator.Generate(source).First(m => m.Kind == "return-default");
        mutant.Replacement.ShouldBe("default!");
        mutant.ApplyTo(source).ShouldBe("class C { int M() { return default!; } }");
    }

    [Fact]
    public void Generate_RemovesAVoidCallWithAnEmptyStatement()
    {
        const string source = "class C { void M() { System.Console.WriteLine(\"x\"); } }";
        var mutant = MutantGenerator.Generate(source).First(m => m.Kind == "call-removal");
        mutant.ApplyTo(source).ShouldBe("class C { void M() { ; } }");
    }

    [Fact]
    public void Generate_KeepsTheEmptyStatementFormSoAnEmbeddedCallStaysValid()
    {
        const string source = "class C { void M(bool ready) { if (ready) Go(); } void Go() { } }";
        var mutant = MutantGenerator.Generate(source).First(m => m.Kind == "call-removal");
        mutant.ApplyTo(source).ShouldContain("if (ready) ;");
    }

    [Fact]
    public void Generate_OnlyMutatesInsideTheChangedLines()
    {
        const string source = """
            class C
            {
                bool A(int a, int b) => a < b;
                bool B(int a, int b) => a > b;
            }
            """;

        var mutants = MutantGenerator.Generate(source, [new LineRange(3, 3)]);

        mutants.ShouldNotBeEmpty();
        mutants.ShouldAllBe(m => m.Line == 3);
        mutants.ShouldAllBe(m => !m.Description.Contains("a > b"));
    }

    [Fact]
    public void Generate_WithNoChangedLines_MutatesTheWholeFile()
    {
        const string source = "class C\n{\n    bool A(int a, int b) => a < b;\n    bool B(int a, int b) => a > b;\n}";
        MutantGenerator.Generate(source).Select(m => m.Line).Distinct().ShouldBe([3, 4]);
    }

    [Fact]
    public void Generate_ReportsOneBasedLineAndColumn()
    {
        const string source = "class C { bool M(int a, int b) => a < b; }";
        var mutant = MutantGenerator.Generate(source).First(m => m.Kind == "relational");
        mutant.Line.ShouldBe(1);
        mutant.Column.ShouldBe(source.IndexOf('<') + 1);
    }

    [Fact]
    public void Generate_CapsTheNumberOfMutants()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, 40).Select(i => $"class C{i} {{ bool M(int a, int b) => a < b; }}"));

        MutantGenerator.Generate(source, maxMutants: 3).Count.ShouldBe(3);
        MutantGenerator.Generate(source).Count.ShouldBe(MutantGenerator.MaxMutants);
    }

    [Fact]
    public void Generate_OnEmptySource_ReturnsNothing()
    {
        MutantGenerator.Generate("").ShouldBeEmpty();
    }

    [Fact]
    public void FromUnifiedDiff_ReadsNewSideHunkSpans()
    {
        const string diff = """
            diff --git a/x.cs b/x.cs
            --- a/x.cs
            +++ b/x.cs
            @@ -1,2 +3,4 @@ class C
            +a
            +b
            @@ -10 +14,0 @@
            -gone
            """;

        LineRange.FromUnifiedDiff(diff).ShouldBe([new LineRange(3, 6)]);
    }

    [Fact]
    public void FromUnifiedDiff_OnAnEmptyDiff_ReturnsNothing()
    {
        LineRange.FromUnifiedDiff("").ShouldBeEmpty();
        LineRange.FromUnifiedDiff(null).ShouldBeEmpty();
    }
}
