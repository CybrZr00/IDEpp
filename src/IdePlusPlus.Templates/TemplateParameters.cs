namespace IdePlusPlus.Templates;

/// <summary>One parameter and whatever the user chose for it. A null or blank
/// <paramref name="Value"/> means "not set", which is not the same as an empty string.</summary>
public sealed record TemplateParameterValue(TemplateParameter Parameter, string? Value);

/// <summary>
/// Which of a template's parameters are worth putting in front of someone, and how the answers turn
/// into command-line arguments.
/// </summary>
public static class TemplateParameters
{
    /// <summary>
    /// Parameters the dialogs already handle with a dedicated control, or that exist for the engine
    /// rather than the user. Showing these again would offer two ways to set one thing, and let them
    /// disagree.
    /// </summary>
    private static readonly HashSet<string> HandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
    {
        // The Language picker and the Framework dropdown.
        "language",
        TemplateInfo.FrameworkParameterName,

        // The namespace is computed from the target folder - see NamespaceResolver.
        TemplateInfo.NamespaceParameterName,

        // "project"/"item"/"solution", which is what the template *is*, not a choice.
        "type",

        // An escape hatch for a raw TFM string, sitting next to a Framework dropdown that offers the
        // real choices. Offering both invites setting one and meaning the other.
        "TargetFrameworkOverride",
    };

    /// <summary>The parameters to render, in the order the template declares them. Excludes the name
    /// parameter (the dialog's own Name field) and everything in
    /// <see cref="HandledElsewhere"/>.</summary>
    public static IReadOnlyList<TemplateParameter> UserEditable(TemplateInfo template) =>
        template.Parameters
            .Where(p => !p.IsName && !HandledElsewhere.Contains(p.Name))
            .ToList();

    /// <summary>
    /// Turns chosen values into the parameter dictionary <see cref="TemplateCreationRequest"/> takes.
    ///
    /// <para>A value equal to the template's own default is omitted rather than passed: the result is
    /// identical and the command line stays readable, which matters because it is echoed into the
    /// Output pane. A bool that is on becomes a switch (<c>--actions</c>); one explicitly turned off
    /// against a default of on becomes <c>--actions false</c>, which the CLI accepts - verified all
    /// three forms against the API Controller template.</para>
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ToArguments(IEnumerable<TemplateParameterValue> values)
    {
        var arguments = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var (parameter, value) in values)
        {
            if (string.IsNullOrWhiteSpace(value) || IsDefault(parameter, value))
            {
                continue;
            }

            arguments[parameter.Name] =
                parameter.DataType == TemplateParameterDataType.Bool && IsTrue(value)
                    ? null
                    : value;
        }

        return arguments;
    }

    /// <summary>Whether every required parameter has been answered - what gates the Create button.</summary>
    public static bool AllRequiredAnswered(IEnumerable<TemplateParameterValue> values) =>
        values.All(v => !v.Parameter.IsRequired || !string.IsNullOrWhiteSpace(v.Value));

    private static bool IsDefault(TemplateParameter parameter, string value) =>
        parameter.DefaultValue is { } defaultValue &&
        (string.Equals(defaultValue, value, StringComparison.Ordinal) ||
         (parameter.DataType == TemplateParameterDataType.Bool && IsTrue(defaultValue) == IsTrue(value)));

    private static bool IsTrue(string value) => bool.TryParse(value, out var parsed) && parsed;
}
