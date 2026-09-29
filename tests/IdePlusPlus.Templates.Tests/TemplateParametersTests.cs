using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class TemplateParametersTests
{
    private static TemplateInfo ApiController => SampleCache.Template("Microsoft.AspNetCore.Mvc.ApiController.10.0");

    private static TemplateInfo ClassLibrary => SampleCache.Template("Microsoft.Common.Library.CSharp.10.0");

    private static TemplateParameter Parameter(TemplateInfo template, string name) =>
        template.FindParameter(name)!;

    /// <summary>The API Controller template declares name/language/type/namespace/actions. Only
    /// "actions" is a real choice - the rest are the dialog's own fields or engine tags.</summary>
    [Fact]
    public void OnlyTheGenuineChoicesAreOffered() =>
        TemplateParameters.UserEditable(ApiController).Select(p => p.Name).ShouldBe(["actions"]);

    [Fact]
    public void FrameworkAndItsOverrideAreBothLeftToTheDropdown() =>
        TemplateParameters.UserEditable(ClassLibrary).Select(p => p.Name)
            .ShouldBe(["langVersion", "skipRestore"]);

    [Fact]
    public void DeclarationOrderIsKept() =>
        TemplateParameters.UserEditable(ClassLibrary).Select(p => p.Name).First().ShouldBe("langVersion");

    [Fact]
    public void NothingChosenPassesNothing() =>
        TemplateParameters.ToArguments([new TemplateParameterValue(Parameter(ApiController, "actions"), null)])
            .ShouldBeEmpty();

    /// <summary>Identical result, shorter command line - and the command line is echoed into the
    /// Output pane, so it is worth keeping readable.</summary>
    [Fact]
    public void AValueEqualToTheTemplatesDefaultPassesNothing() =>
        TemplateParameters.ToArguments([new TemplateParameterValue(Parameter(ApiController, "actions"), "false")])
            .ShouldBeEmpty();

    /// <summary>A bool that is on is a switch: `--actions`, not `--actions true`.</summary>
    [Fact]
    public void ABoolTurnedOnBecomesASwitch()
    {
        var arguments = TemplateParameters.ToArguments(
            [new TemplateParameterValue(Parameter(ApiController, "actions"), "true")]);

        arguments.ShouldContainKey("actions");
        arguments["actions"].ShouldBeNull();
    }

    /// <summary>skipRestore also defaults to false, so turning it on is the same shape - this pins
    /// that the rule is about the data type, not about one template.</summary>
    [Fact]
    public void AnotherBoolBehavesTheSameWay() =>
        TemplateParameters.ToArguments(
            [new TemplateParameterValue(Parameter(ClassLibrary, "skipRestore"), "true")])
            .ShouldContainKey("skipRestore");

    [Fact]
    public void ATextValuePassesThrough()
    {
        var arguments = TemplateParameters.ToArguments(
            [new TemplateParameterValue(Parameter(ClassLibrary, "langVersion"), "preview")]);

        arguments["langVersion"].ShouldBe("preview");
    }

    /// <summary>Turning off something whose default is on has to be said explicitly - omitting it
    /// would leave the default in place, which is the opposite of what was asked.</summary>
    [Fact]
    public void ABoolTurnedOffAgainstADefaultOfOnIsPassedExplicitly()
    {
        var defaultOn = Parameter(ApiController, "actions") with { DefaultValue = "true" };

        TemplateParameters.ToArguments([new TemplateParameterValue(defaultOn, "false")])["actions"]
            .ShouldBe("false");
    }

    [Fact]
    public void RequiredParametersGateCreation()
    {
        var required = Parameter(ClassLibrary, "langVersion") with { IsRequired = true };

        TemplateParameters.AllRequiredAnswered([new TemplateParameterValue(required, null)]).ShouldBeFalse();
        TemplateParameters.AllRequiredAnswered([new TemplateParameterValue(required, "preview")]).ShouldBeTrue();
    }

    [Fact]
    public void OptionalParametersDoNotGateCreation() =>
        TemplateParameters.AllRequiredAnswered(
            [new TemplateParameterValue(Parameter(ClassLibrary, "langVersion"), null)]).ShouldBeTrue();
}
