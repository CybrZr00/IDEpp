using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

/// <summary>
/// Which of the two namespace routes each template takes. Getting this backwards is silent in both
/// directions: passing <c>--namespace</c> to a template that doesn't declare it is a hard CLI error,
/// and not passing it to one that does leaves the literal "MyApp.Namespace" in the generated file.
/// </summary>
public class ItemCreationPlanTests
{
    private const string ProjectDirectory = @"C:\repo\Demo";

    private static TemplateInfo Class => SampleCache.Template("DotNet.Common.Class.CSharp");

    private static TemplateInfo ApiController => SampleCache.Template("Microsoft.AspNetCore.Mvc.ApiController.10.0");

    [Fact]
    public void ATemplateWithANamespaceParameterIsPassedTheNamespace()
    {
        var plan = ItemCreationPlan.For(
            ApiController, "ThingController", @"C:\repo\Demo\Controllers", ProjectDirectory, "Demo");

        plan.Request.Namespace.ShouldBe("Demo.Controllers");
        plan.CorrectAfterCreation.ShouldBeFalse();
    }

    [Fact]
    public void ATemplateWithoutOneIsCorrectedAfterwards()
    {
        var plan = ItemCreationPlan.For(Class, "Widget", @"C:\repo\Demo\Models", ProjectDirectory, "Demo");

        plan.Request.Namespace.ShouldBeNull();
        plan.CorrectAfterCreation.ShouldBeTrue();
        plan.DesiredNamespace.ShouldBe("Demo.Models");
        plan.BoundNamespace.ShouldBe("Demo");
    }

    /// <summary>At the project root the template's own binding already produces the right answer, so
    /// there is nothing to rewrite - and rewriting anyway would mean touching every generated file
    /// for no reason.</summary>
    [Fact]
    public void NothingIsCorrectedForAFileAtTheProjectRoot()
    {
        var plan = ItemCreationPlan.For(Class, "Widget", ProjectDirectory, ProjectDirectory, "Demo");

        plan.CorrectAfterCreation.ShouldBeFalse();
        plan.DesiredNamespace.ShouldBe("Demo");
    }

    /// <summary>The controller templates still need the namespace passed at the project root: left
    /// unset they emit their parameter's default, "MyApp.Namespace", not the project's.</summary>
    [Fact]
    public void ANamespaceIsStillPassedAtTheProjectRoot() =>
        ItemCreationPlan.For(ApiController, "ThingController", ProjectDirectory, ProjectDirectory, "Demo")
            .Request.Namespace.ShouldBe("Demo");

    [Fact]
    public void TheRequestCarriesTheNameAndTarget()
    {
        var plan = ItemCreationPlan.For(Class, "Widget", @"C:\repo\Demo\Models", ProjectDirectory, "Demo");

        plan.Request.ShortName.ShouldBe("class");
        plan.Request.Name.ShouldBe("Widget");
        plan.Request.OutputDirectory.ShouldBe(@"C:\repo\Demo\Models");
    }

    [Fact]
    public void ALanguageIsPassedThroughWhenGiven() =>
        ItemCreationPlan.For(Class, "Widget", ProjectDirectory, ProjectDirectory, "Demo", "VB")
            .Request.Language.ShouldBe("VB");

    /// <summary>Every item plan names its project, so the template's project-capability constraint
    /// resolves even when the target folder does not exist yet.</summary>
    [Fact]
    public void ThePlanCarriesTheProject() =>
        ItemCreationPlan.For(
            Class, "Widget", @"C:\repo\Demo\New\Folder", ProjectDirectory, "Demo",
            projectPath: @"C:\repo\Demo\Demo.csproj")
            .Request.ProjectPath.ShouldBe(@"C:\repo\Demo\Demo.csproj");

    [Fact]
    public void NestedFoldersReachTheDesiredNamespace() =>
        ItemCreationPlan.For(Class, "Widget", @"C:\repo\Demo\Api\V2", ProjectDirectory, "Demo")
            .DesiredNamespace.ShouldBe("Demo.Api.V2");
}
