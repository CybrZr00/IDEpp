using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public class TemplateToolsTests
{
    [Fact]
    public void AllThreeToolsRouteAsBuiltIn()
    {
        foreach (var tool in TemplateTools.All)
        {
            BuiltInTools.IsBuiltIn(tool.Name).ShouldBeTrue($"'{tool.Name}' is offered but would route to MCP");
            TemplateTools.IsTemplateTool(tool.Name).ShouldBeTrue();
        }
    }

    [Fact]
    public void IsTemplateTool_DoesNotClaimOtherTools() =>
        TemplateTools.IsTemplateTool(PublishTools.PublishToolName).ShouldBeFalse();

    /// <summary>Both writing tools need a template and a name; without them the model would have to
    /// discover that by failing a call.</summary>
    [Theory]
    [InlineData(TemplateTools.CreateProjectToolName)]
    [InlineData(TemplateTools.AddItemToolName)]
    public void TheWritingToolsRequireATemplateAndAName(string toolName)
    {
        var schema = TemplateTools.All.Single(t => t.Name == toolName).ParametersSchema;
        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        required.ShouldBe(["template", "name"]);
    }

    /// <summary>Listing is read-only, so nothing about it is required.</summary>
    [Fact]
    public void ListingRequiresNothing() =>
        TemplateTools.ListTemplates.ParametersSchema["required"].ShouldBeNull();

    /// <summary>
    /// The three refusals are stated in the descriptions rather than left to be discovered by a
    /// failed call - installing a package, running a solution template, and escaping the solution
    /// directory.
    /// </summary>
    [Fact]
    public void TheRefusalsAreStatedUpFront()
    {
        TemplateTools.CreateProject.Description.ShouldContain("solution templates");
        TemplateTools.CreateProject.Description.ShouldContain("Template Packages");
        TemplateTools.AddItem.Description.ShouldContain("inside the project");
    }

    /// <summary>Installing a template package is user-initiated - there must be no tool for it.</summary>
    [Fact]
    public void ThereIsNoInstallTool() =>
        BuiltInTools.All.Concat(BuiltInTools.OptIn).Concat(BuiltInTools.WebOptIn)
            .ShouldNotContain(t => t.Name.Contains("install", StringComparison.OrdinalIgnoreCase));
}
