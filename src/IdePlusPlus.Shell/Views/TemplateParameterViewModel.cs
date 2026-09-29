using CommunityToolkit.Mvvm.ComponentModel;
using IdePlusPlus.Templates;

namespace IdePlusPlus.Shell.Views;

/// <summary>
/// One template parameter as a row in the New Project / New Item dialogs, rendered from what the
/// template cache says about it rather than from any per-template knowledge: a bool is a checkbox, a
/// choice is a combo box, anything else is a text box.
///
/// <para>The three value properties are deliberately separate rather than one shared string. A
/// checkbox binds to a bool and a combo box to the selected item, and funnelling both through one
/// string property means converters in the XAML and a value that is briefly neither - which is how
/// a parameter ends up passed as "System.Object".</para>
/// </summary>
public sealed partial class TemplateParameterViewModel : ObservableObject
{
    public TemplateParameterViewModel(TemplateParameter parameter)
    {
        Parameter = parameter;
        Choices = parameter.Choices.Select(c => c.Value).ToList();

        boolValue = bool.TryParse(parameter.DefaultValue, out var parsed) && parsed;
        textValue = IsChoice ? string.Empty : parameter.DefaultValue ?? string.Empty;
        selectedChoice = IsChoice
            ? Choices.FirstOrDefault(c => c == parameter.DefaultValue) ?? Choices.FirstOrDefault()
            : null;
    }

    public TemplateParameter Parameter { get; }

    public IReadOnlyList<string> Choices { get; }

    public string Label => Parameter.Label;

    /// <summary>The cache stores the same text in Description and Documentation for most templates;
    /// either is fine as a tooltip, and blank is fine too.</summary>
    public string? Description => Parameter.Description;

    public bool IsBool => Parameter.DataType == TemplateParameterDataType.Bool;

    public bool IsChoice => Parameter.DataType == TemplateParameterDataType.Choice && Choices.Count > 0;

    public bool IsText => !IsBool && !IsChoice;

    public bool IsRequired => Parameter.IsRequired;

    [ObservableProperty]
    private bool boolValue;

    [ObservableProperty]
    private string textValue;

    [ObservableProperty]
    private string? selectedChoice;

    /// <summary>What the user has actually chosen, in the one shape
    /// <see cref="TemplateParameters.ToArguments"/> takes.</summary>
    public TemplateParameterValue ToValue() => new(
        Parameter,
        IsBool ? BoolValue.ToString().ToLowerInvariant()
        : IsChoice ? SelectedChoice
        : TextValue);

    partial void OnBoolValueChanged(bool value) => Changed?.Invoke();

    partial void OnTextValueChanged(string value) => Changed?.Invoke();

    partial void OnSelectedChoiceChanged(string? value) => Changed?.Invoke();

    /// <summary>Raised on any edit, so the owning dialog can re-check whether its required
    /// parameters are answered.</summary>
    public event Action? Changed;
}
