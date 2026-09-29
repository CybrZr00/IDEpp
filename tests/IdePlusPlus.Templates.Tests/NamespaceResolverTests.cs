using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class NamespaceResolverTests
{
    private const string ProjectDirectory = @"C:\repo\Demo";

    [Fact]
    public void AFileAtTheProjectRootGetsTheRootNamespace() =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, ProjectDirectory).ShouldBe("Demo");

    /// <summary>The case that motivates this class: `dotnet new class -o Models` emits
    /// "namespace Demo;" and Visual Studio would have written "Demo.Models".</summary>
    [Fact]
    public void AFileInAFolderGetsTheFolderAppended() =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, @"C:\repo\Demo\Models").ShouldBe("Demo.Models");

    [Fact]
    public void NestedFoldersAllAppend() =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, @"C:\repo\Demo\Api\V2\Models")
            .ShouldBe("Demo.Api.V2.Models");

    [Fact]
    public void ADottedRootNamespaceIsKept() =>
        NamespaceResolver.Resolve("IdePlusPlus.Templates", ProjectDirectory, @"C:\repo\Demo\Models")
            .ShouldBe("IdePlusPlus.Templates.Models");

    [Fact]
    public void ATrailingSeparatorOnTheProjectDirectoryDoesNotAddAnEmptySegment() =>
        NamespaceResolver.Resolve("Demo", @"C:\repo\Demo\", @"C:\repo\Demo\Models").ShouldBe("Demo.Models");

    /// <summary>Adding a file outside the project has no folder chain to fold in, so the root
    /// namespace is the best answer available - and certainly better than "Demo....".</summary>
    [Fact]
    public void ATargetOutsideTheProjectFallsBackToTheRootNamespace() =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, @"C:\repo\Elsewhere").ShouldBe("Demo");

    [Theory]
    [InlineData("my-models", "my_models")]
    [InlineData("my models", "my_models")]
    [InlineData("my.models", "my_models")]
    [InlineData("Models+", "Models_")]
    public void FolderNamesThatArentIdentifiersAreSanitized(string folder, string expected) =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, Path.Combine(ProjectDirectory, folder))
            .ShouldBe($"Demo.{expected}");

    [Fact]
    public void AFolderStartingWithADigitGetsAnUnderscore() =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, @"C:\repo\Demo\2024").ShouldBe("Demo._2024");

    /// <summary>A folder genuinely named "event" is legal on disk. The verbatim form keeps the name
    /// and compiles; Visual Studio emits the bare keyword and produces a file that doesn't build.</summary>
    [Theory]
    [InlineData("event")]
    [InlineData("class")]
    [InlineData("object")]
    [InlineData("internal")]
    public void AFolderNamedAfterAKeywordBecomesAVerbatimIdentifier(string keyword) =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, Path.Combine(ProjectDirectory, keyword))
            .ShouldBe($"Demo.@{keyword}");

    /// <summary>Keywords are lowercase-only, so an ordinary folder name is untouched.</summary>
    [Fact]
    public void AFolderMerelyCapitalisedLikeAKeywordIsLeftAlone() =>
        NamespaceResolver.Resolve("Demo", ProjectDirectory, @"C:\repo\Demo\Events").ShouldBe("Demo.Events");

    [Theory]
    [InlineData("Models", "Models")]
    [InlineData("2024", "_2024")]
    [InlineData("a-b", "a_b")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SanitizeSegmentHandlesEachCaseOnItsOwn(string input, string expected) =>
        NamespaceResolver.SanitizeSegment(input).ShouldBe(expected);
}
