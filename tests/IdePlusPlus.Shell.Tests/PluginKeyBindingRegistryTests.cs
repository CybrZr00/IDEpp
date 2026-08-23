using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// PluginKeyBindingRegistry's one real piece of logic: never letting a plugin silently shadow a
/// gesture MainWindow.xaml's static Window.InputBindings (or its Find/Replace menu commands) already
/// own.
/// </summary>
public sealed class PluginKeyBindingRegistryTests
{
    [Fact]
    public void TryRegister_ReservedGesture_IsRejectedAndLogged()
    {
        var output = new OutputPaneViewModel();
        var registry = new PluginKeyBindingRegistry(output);

        var executed = false;
        var accepted = registry.TryRegister("Some Plugin", "F5", "Do a thing", () => executed = true);

        accepted.ShouldBeFalse();
        registry.Bindings.ShouldBeEmpty();
        output.Text.ShouldContain("Some Plugin");
        output.Text.ShouldContain("F5");
        output.Text.ShouldContain("reserved");
        executed.ShouldBeFalse();
    }

    [Fact]
    public void TryRegister_NonReservedGesture_IsAccepted()
    {
        var output = new OutputPaneViewModel();
        var registry = new PluginKeyBindingRegistry(output);

        PluginKeyBinding? raised = null;
        registry.Registered += binding => raised = binding;

        var accepted = registry.TryRegister("Some Plugin", "Ctrl+Alt+P", "Do a thing", () => { });

        accepted.ShouldBeTrue();
        registry.Bindings.Count.ShouldBe(1);
        registry.Bindings[0].PluginName.ShouldBe("Some Plugin");
        registry.Bindings[0].GestureText.ShouldBe("Ctrl+Alt+P");
        registry.Bindings[0].Header.ShouldBe("Do a thing");
        raised.ShouldBe(registry.Bindings[0]);
    }

    [Theory]
    [InlineData("F5")]
    [InlineData("Ctrl+F5")]
    [InlineData("F10")]
    [InlineData("F11")]
    [InlineData("Shift+F11")]
    [InlineData("Ctrl+G")]
    [InlineData("Ctrl+F")]
    [InlineData("Ctrl+H")]
    public void TryRegister_EveryReservedGesture_IsRejected(string gestureText)
    {
        var registry = new PluginKeyBindingRegistry(new OutputPaneViewModel());

        registry.TryRegister("Some Plugin", gestureText, "Do a thing", () => { }).ShouldBeFalse();
        registry.Bindings.ShouldBeEmpty();
    }

    [Fact]
    public void TryRegister_UnparsableGesture_IsRejectedAndLogged()
    {
        var output = new OutputPaneViewModel();
        var registry = new PluginKeyBindingRegistry(output);

        registry.TryRegister("Some Plugin", "NotAGesture!!", "Do a thing", () => { }).ShouldBeFalse();
        registry.Bindings.ShouldBeEmpty();
        output.Text.ShouldContain("Some Plugin");
    }
}
