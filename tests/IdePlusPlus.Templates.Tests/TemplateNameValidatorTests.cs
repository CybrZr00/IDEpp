using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class TemplateNameValidatorTests
{
    [Theory]
    [InlineData("MyLibrary")]
    [InlineData("IdePlusPlus.Templates")]
    [InlineData("my-library")]
    [InlineData("Lib_1")]
    public void OrdinaryProjectNamesAreAccepted(string name) =>
        TemplateNameValidator.ValidateProjectName(name).ShouldBeNull();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyNameIsRejected(string? name) =>
        TemplateNameValidator.ValidateProjectName(name).ShouldNotBeNull();

    [Theory]
    [InlineData("My/Library")]
    [InlineData("My\\Library")]
    [InlineData("My:Library")]
    [InlineData("My*Library")]
    [InlineData("My?Library")]
    [InlineData("My|Library")]
    public void PathCharactersAreRejected(string name) =>
        TemplateNameValidator.ValidateProjectName(name).ShouldNotBeNull();

    /// <summary>Windows strips these silently, so the project would land somewhere other than the
    /// path the dialog showed.</summary>
    [Theory]
    [InlineData("MyLibrary.")]
    [InlineData("MyLibrary ")]
    public void TrailingDotsAndSpacesAreRejected(string name) =>
        TemplateNameValidator.ValidateProjectName(name).ShouldNotBeNull();

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("aux")]
    public void ReservedDeviceNamesAreRejected(string name) =>
        TemplateNameValidator.ValidateProjectName(name).ShouldNotBeNull();

    /// <summary>CON.cs is as unusable as CON - the reservation is on the stem, not the whole
    /// name.</summary>
    [Fact]
    public void AReservedNameWithAnExtensionIsAlsoRejected() =>
        TemplateNameValidator.ValidateFileName("CON.cs").ShouldNotBeNull();

    [Fact]
    public void ANameThatMerelyStartsWithAReservedWordIsFine() =>
        TemplateNameValidator.ValidateProjectName("Console").ShouldBeNull();

    [Theory]
    [InlineData("Widget")]
    [InlineData("_Widget")]
    [InlineData("Widget1")]
    [InlineData("widget_two")]
    public void OrdinaryTypeNamesAreAccepted(string name) =>
        TemplateNameValidator.ValidateTypeName(name).ShouldBeNull();

    /// <summary>The one rule that differs from a project name: a project can be
    /// IdePlusPlus.Templates, a class cannot.</summary>
    [Theory]
    [InlineData("My.Widget")]
    [InlineData("My-Widget")]
    [InlineData("1Widget")]
    [InlineData("My Widget")]
    public void TypeNamesThatAreNotIdentifiersAreRejected(string name) =>
        TemplateNameValidator.ValidateTypeName(name).ShouldNotBeNull();

    [Fact]
    public void TypeNameValidationStillAppliesTheFileNameRules() =>
        TemplateNameValidator.ValidateTypeName("CON").ShouldNotBeNull();
}
