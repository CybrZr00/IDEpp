namespace IdePlusPlus.Templates;

/// <summary>
/// The parameter data types the SDK's template cache actually uses. Counted across the installed
/// cache: choice, string, bool, integer, text, and 33 parameters with no data type at all - hence
/// <see cref="Unknown"/>.
/// </summary>
public enum TemplateParameterDataType
{
    Unknown,
    String,
    Text,
    Bool,
    Integer,
    Choice,
}

/// <summary>One option of a <see cref="TemplateParameterDataType.Choice"/> parameter.</summary>
/// <param name="Value">What gets passed on the command line.</param>
/// <param name="DisplayName">The engine stores an empty string rather than omitting this, so it is
/// blank far more often than it is useful - callers should fall back to <paramref name="Value"/>.</param>
public sealed record TemplateParameterChoice(string Value, string? DisplayName, string? Description)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Value : DisplayName!;
}

/// <param name="IsName">True for the parameter that receives <c>--name</c>. Every template has one,
/// and it is passed as <c>-n</c> rather than by name.</param>
public sealed record TemplateParameter(
    string Name,
    string? DisplayName,
    string? Description,
    TemplateParameterDataType DataType,
    IReadOnlyList<TemplateParameterChoice> Choices,
    string? DefaultValue,
    bool IsRequired,
    bool IsName)
{
    /// <summary>What to show beside the input. Blank display names are stored as empty strings, not
    /// omitted, so this cannot just be a null-coalesce.</summary>
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;
}
