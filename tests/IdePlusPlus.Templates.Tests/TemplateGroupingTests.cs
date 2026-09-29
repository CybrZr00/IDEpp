using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

/// <summary>
/// The duplicate-collapsing that makes the template list usable at all, and the framework resolution
/// that stops it silently producing the wrong target framework.
/// </summary>
public class TemplateGroupingTests
{
    private const string ClassLibrary = "Microsoft.Common.Library";
    private const string Class = "DotNet.Common.Class";
    private const string ApiController = "Microsoft.AspNetCore.Mvc.ApiController";
    private const string NUnitItem = "NUnit3.DotNetNew.ItemTemplate";

    [Fact]
    public void TwelveEntriesCollapseToSixRows() =>
        TemplateGrouping.Group(SampleCache.Templates).Count.ShouldBe(6);

    [Fact]
    public void ThreeClassLibraryEntriesBecomeOneRow()
    {
        var group = SampleCache.Group(ClassLibrary);

        group.Templates.Count.ShouldBe(3);
        group.Name.ShouldBe("Class Library");
        group.ShortName.ShouldBe("classlib");
    }

    /// <summary>The row has to describe itself the way `dotnet new classlib` behaves, which is the
    /// highest-precedence entry - not whichever one the cache happened to list first.</summary>
    [Fact]
    public void TheRowTakesItsNameFromTheHighestPrecedenceEntry() =>
        SampleCache.Group(NUnitItem).Name.ShouldBe("NUnit Test Item");

    [Fact]
    public void GroupsAreOrderedByName() =>
        TemplateGrouping.Group(SampleCache.Templates).Select(g => g.Name)
            .ShouldBe(
            [
                "API Controller",
                "Aspire Starter App (ASP.NET Core/Blazor)",
                "Class",
                "Class Library",
                "EditorConfig file",
                "NUnit Test Item",
            ]);

    [Fact]
    public void LanguageVariantsShareOneRow() =>
        SampleCache.Group(Class).Languages.ShouldBe(["C#", "VB"]);

    [Fact]
    public void ATemplateWithNoLanguageHasNone() =>
        SampleCache.Group("Microsoft.Standard.QuickStarts.EditorConfigFile").Languages.ShouldBeEmpty();

    /// <summary>
    /// The union no single cache entry offers: the 10.0 entry knows net10.0, the 8.0 entry net8.0, and
    /// the dropdown has to show both. Newest band first, since that is the one `dotnet new` defaults to.
    /// </summary>
    [Fact]
    public void FrameworkChoicesAreUnionedAcrossTheBands() =>
        SampleCache.Group(ClassLibrary).FrameworkChoicesFor("C#")
            .ShouldBe(["net10.0", "netstandard2.1", "netstandard2.0", "net9.0", "net8.0"]);

    /// <summary>The reason every entry is kept rather than filtered down to the winner. Asking for
    /// net8.0 has to select the 8.0-band template; passing --framework net8.0 to the 10.0 one is not
    /// an option it offers.</summary>
    [Theory]
    [InlineData("net8.0", "Microsoft.Common.Library.CSharp.8.0")]
    [InlineData("net9.0", "Microsoft.Common.Library.CSharp.9.0")]
    [InlineData("net10.0", "Microsoft.Common.Library.CSharp.10.0")]
    public void AFrameworkSelectsTheBandThatOffersIt(string framework, string expectedIdentity) =>
        SampleCache.Group(ClassLibrary).Resolve("C#", framework)!.Identity.ShouldBe(expectedIdentity);

    /// <summary>netstandard2.0 is offered by all three bands, so precedence breaks the tie exactly as
    /// `dotnet new` would.</summary>
    [Fact]
    public void AFrameworkOfferedByEveryBandResolvesToTheHighestPrecedence() =>
        SampleCache.Group(ClassLibrary).Resolve("C#", "netstandard2.0")!.Identity
            .ShouldBe("Microsoft.Common.Library.CSharp.10.0");

    [Fact]
    public void NoFrameworkResolvesToTheHighestPrecedence() =>
        SampleCache.Group(ClassLibrary).Resolve("C#")!.Identity
            .ShouldBe("Microsoft.Common.Library.CSharp.10.0");

    /// <summary>Better to create something than to refuse over a framework the group never offered -
    /// the dialog only ever shows frameworks from FrameworkChoicesFor, so this is a guard, not a path.</summary>
    [Fact]
    public void AnUnknownFrameworkFallsBackToTheHighestPrecedence() =>
        SampleCache.Group(ClassLibrary).Resolve("C#", "net4.8")!.Identity
            .ShouldBe("Microsoft.Common.Library.CSharp.10.0");

    [Fact]
    public void ResolvingByLanguagePicksThatLanguagesEntry()
    {
        SampleCache.Group(Class).Resolve("VB")!.Identity.ShouldBe("DotNet.Common.Class.VisualBasic");
        SampleCache.Group(Class).Resolve("C#")!.Identity.ShouldBe("DotNet.Common.Class.CSharp");
    }

    [Fact]
    public void ResolvingALanguageTheGroupDoesNotHaveGivesNothing() =>
        SampleCache.Group(Class).Resolve("F#").ShouldBeNull();

    [Fact]
    public void FilteringByKindKeepsOnlyThatKind() =>
        TemplateGrouping.Group(SampleCache.Templates, TemplateKind.Item).Select(g => g.Name)
            .ShouldBe(["API Controller", "Class", "EditorConfig file", "NUnit Test Item"]);

    [Fact]
    public void FilteringByKindFindsTheSolutionTemplates() =>
        TemplateGrouping.Group(SampleCache.Templates, TemplateKind.Solution).Select(g => g.GroupIdentity)
            .ShouldBe(["Aspire.Starter"]);

    [Fact]
    public void AnEmptyQueryKeepsEverything() =>
        TemplateGrouping.Search(TemplateGrouping.Group(SampleCache.Templates), "  ").Count.ShouldBe(6);

    /// <summary>"class" reaching NUnit Test Item ("Creates a new NUnit test class") is the point of
    /// searching descriptions too, not an over-match to tighten up.</summary>
    [Theory]
    [InlineData("class", new[] { "Class", "Class Library", "NUnit Test Item" })]
    [InlineData("classlib", new[] { "Class Library" })]
    [InlineData("controller", new[] { "API Controller" })]
    [InlineData("nothing here", new string[0])]
    public void SearchMatchesNamesShortNamesAndDescriptions(string query, string[] expected) =>
        TemplateGrouping.Search(TemplateGrouping.Group(SampleCache.Templates), query)
            .Select(g => g.Name).ShouldBe(expected);

    /// <summary>Someone looking for a template is as likely to search the tag as the title - the SDK's
    /// own table shows tags for exactly that reason. "Cloud" appears nowhere but Aspire Starter's
    /// classifications, so this isolates tag matching from the rest.</summary>
    [Fact]
    public void SearchMatchesClassifications() =>
        TemplateGrouping.Search(TemplateGrouping.Group(SampleCache.Templates), "Cloud")
            .Select(g => g.Name).ShouldBe(["Aspire Starter App (ASP.NET Core/Blazor)"]);

    [Fact]
    public void SearchTermsAreAndedTogether() =>
        TemplateGrouping.Search(TemplateGrouping.Group(SampleCache.Templates), "class library")
            .Select(g => g.Name).ShouldBe(["Class Library"]);
}
