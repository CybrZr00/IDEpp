using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class ItemTemplateShortlistTests
{
    private static IReadOnlyList<TemplateGroup> ItemGroups =>
        TemplateGrouping.Group(SampleCache.Templates, TemplateKind.Item);

    /// <summary>The sample holds Class and API Controller but not Enum or Record, so this also
    /// covers "don't offer what isn't installed".</summary>
    [Fact]
    public void KeepsShortlistOrderRatherThanCatalogOrder() =>
        ItemTemplateShortlist.From(ItemGroups).Select(g => g.Name)
            .ShouldBe(["Class", "API Controller"]);

    [Fact]
    public void SkipsTemplatesTheMachineDoesNotHave() =>
        ItemTemplateShortlist.From([]).ShouldBeEmpty();

    [Fact]
    public void TheShortlistIsTheOnesAskedFor() =>
        ItemTemplateShortlist.ShortNames.ShouldBe(["class", "interface", "enum", "record", "apicontroller"]);

    /// <summary>Drives which validator the dialog uses - a Class name has to be an identifier, an
    /// .editorconfig name does not.</summary>
    [Fact]
    public void TypeProducingTemplatesAreDistinguishedFromConfigFiles()
    {
        SampleCache.Template("DotNet.Common.Class.CSharp").NameBecomesTypeName.ShouldBeTrue();
        SampleCache.Template("Microsoft.AspNetCore.Mvc.ApiController.10.0").NameBecomesTypeName.ShouldBeTrue();
        SampleCache.Template("Microsoft.Standard.QuickStarts.EditorConfigFile").NameBecomesTypeName.ShouldBeFalse();
    }
}
