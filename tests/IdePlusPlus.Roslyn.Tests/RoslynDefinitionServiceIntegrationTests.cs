using IdePlusPlus.Editor;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// Proves - against the same real, restored scratch solution as
/// <see cref="RoslynCompletionServiceIntegrationTests"/> - that go-to-definition genuinely resolves
/// through a real Roslyn <c>SymbolFinder</c>, not a stub: a real overload/shadowing-sensitive
/// position-based lookup, not the name-string search RoslynWorkspaceService.FindSymbolsAsync uses.
/// </summary>
[Collection(nameof(RoslynScratchSolutionCollection))]
public sealed class RoslynDefinitionServiceIntegrationTests
{
    private readonly RoslynScratchSolutionFixture _fixture;

    public RoslynDefinitionServiceIntegrationTests(RoslynScratchSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetDefinitionAsync_OnACallToAMethodInTheSameFile_ResolvesToItsDeclarationLine()
    {
        const string source = """
            namespace ScratchLib;

            public class Class1
            {
                public void M()
                {
                    Target();
                }

                public void Target()
                {
                }
            }

            """;

        // The first "Target()" in this layout is the call inside M - the declaration follows it.
        var callOffset = source.IndexOf("Target()", StringComparison.Ordinal);
        callOffset.ShouldBeGreaterThanOrEqualTo(0);

        var declarationLine = Array.FindIndex(source.Split('\n'), line => line.Contains("public void Target()")) + 1;
        declarationLine.ShouldBeGreaterThan(0);

        var definitionService = new RoslynDefinitionService(_fixture.WorkspaceService);

        var outcome = await definitionService.GetDefinitionAsync(_fixture.ClassFilePath, source, callOffset)
            .WaitAsync(TimeSpan.FromSeconds(60));

        var found = outcome.ShouldBeOfType<EditorDefinitionOutcome.Found>();
        found.FilePath.ShouldBe(_fixture.ClassFilePath);
        found.Line.ShouldBe(declarationLine);
        found.Column.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GetDefinitionAsync_OnABclMember_HasNoSourceAvailable()
    {
        const string trigger = "System.String.";
        const string source = """
            namespace ScratchLib;

            public class Class1
            {
                public void M()
                {
                    var s = System.String.IsNullOrEmpty("x");
                }
            }

            """;

        // A few characters into "IsNullOrEmpty", not at its exact boundary with the dot before it.
        var offset = source.IndexOf(trigger, StringComparison.Ordinal) + trigger.Length + 3;

        var definitionService = new RoslynDefinitionService(_fixture.WorkspaceService);

        var outcome = await definitionService.GetDefinitionAsync(_fixture.ClassFilePath, source, offset)
            .WaitAsync(TimeSpan.FromSeconds(60));

        outcome.ShouldBeOfType<EditorDefinitionOutcome.NoSourceAvailable>()
            .SymbolDisplayName.ShouldContain("IsNullOrEmpty");
    }

    [Fact]
    public async Task GetDefinitionAsync_OnAKeyword_ResolvesToNull()
    {
        const string source = """
            namespace ScratchLib;

            public class Class1
            {
                public void M()
                {
                }
            }

            """;

        // A couple of characters into the "class" keyword itself - not "Class1", the actual type
        // name, which would resolve to something real.
        var keywordOffset = source.IndexOf("public class Class1", StringComparison.Ordinal) + "public ".Length + 2;

        var definitionService = new RoslynDefinitionService(_fixture.WorkspaceService);

        var outcome = await definitionService.GetDefinitionAsync(_fixture.ClassFilePath, source, keywordOffset)
            .WaitAsync(TimeSpan.FromSeconds(60));

        outcome.ShouldBeNull();
    }
}
