using System.Linq;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Roslyn.Tests;

/// <summary>
/// Proves - against the same real, restored scratch solution as
/// <see cref="RoslynWorkspaceServiceIntegrationTests"/> - that member-list completion genuinely
/// works end to end through a real Roslyn <c>CompletionService</c>, not a stub.
/// </summary>
[Collection(nameof(RoslynScratchSolutionCollection))]
public sealed class RoslynCompletionServiceIntegrationTests
{
    private readonly RoslynScratchSolutionFixture _fixture;

    public RoslynCompletionServiceIntegrationTests(RoslynScratchSolutionFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GetCompletionsAsync_RightAfterASystemStringMemberAccessDot_ReturnsRealBclStaticMembers()
    {
        const string trigger = "System.String.";
        const string source = """
            namespace ScratchLib;

            public class Class1
            {
                public void M()
                {
                    var s = System.String.
                }
            }

            """;

        var caretOffset = source.IndexOf(trigger, StringComparison.Ordinal) + trigger.Length;
        caretOffset.ShouldBeGreaterThan(trigger.Length - 1); // sanity: trigger text was actually found

        var completionService = new RoslynCompletionService(_fixture.WorkspaceService);

        var completions = await completionService.GetCompletionsAsync(_fixture.ClassFilePath, source, caretOffset)
            .WaitAsync(TimeSpan.FromSeconds(60));

        completions.ShouldNotBeEmpty();

        var displayNames = completions.Select(c => c.DisplayText).ToHashSet(StringComparer.Ordinal);
        // Real, well-known static members of System.String - not a made-up/mocked list.
        displayNames.ShouldContain("Format");
        displayNames.ShouldContain("Concat");
        displayNames.ShouldContain("Empty");
        displayNames.ShouldContain("IsNullOrEmpty");

        foreach (var item in completions)
        {
            item.ReplacementStart.ShouldBeGreaterThanOrEqualTo(0);
            item.ReplacementLength.ShouldBeGreaterThanOrEqualTo(0);
            item.DisplayText.ShouldNotBeNullOrEmpty();
            item.InsertText.ShouldNotBeNullOrEmpty();
        }
    }
}
