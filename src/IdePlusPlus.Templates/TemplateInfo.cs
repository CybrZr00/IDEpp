namespace IdePlusPlus.Templates;

/// <summary>
/// One entry from the SDK's template cache - which is not the same thing as one template as a user
/// would count them. The SDK ships a separate entry per band for the same template (classlib is
/// present three times, one each from the 8.0/9.0/10.0 template packages), distinguished only by
/// <see cref="Precedence"/> and the framework choices it offers. <see cref="TemplateGroup"/> is the
/// user-facing unit; this is the raw row.
/// </summary>
public sealed record TemplateInfo(
    string Identity,
    string GroupIdentity,
    IReadOnlyList<string> ShortNames,
    string Name,
    string? Description,
    string? Author,
    IReadOnlyList<string> Classifications,
    string? Language,
    TemplateKind Kind,
    int Precedence,
    string? DefaultName,
    IReadOnlyList<TemplateParameter> Parameters)
{
    /// <summary>The name to pass to <c>dotnet new create</c>. Several templates declare more than one
    /// (gitignore is also ".gitignore"); the first is the canonical one.</summary>
    public string ShortName => ShortNames.Count > 0 ? ShortNames[0] : Identity;

    public TemplateParameter? FindParameter(string name) =>
        Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when this template takes an explicit namespace. It matters which: apicontroller,
    /// mvccontroller, razorcomponent, page and view all do, and left unset they emit the parameter's
    /// literal default ("MyApp.Namespace") into the generated file. class/interface/enum/record/struct
    /// do not - they bind to msbuild:RootNamespace instead, which is why their namespace has to be
    /// corrected after the fact rather than passed in.
    /// </summary>
    public bool DeclaresNamespaceParameter => FindParameter(NamespaceParameterName) is not null;

    /// <summary>
    /// The target frameworks this particular entry offers. Deliberately not unioned here: each band's
    /// entry offers only its own (the 10.0 classlib entry offers net10.0, the 8.0 one net8.0), and
    /// conflating them would produce a framework the chosen entry cannot actually be asked for. The
    /// union lives on <see cref="TemplateGroup"/>, which also knows how to map back to the entry.
    /// </summary>
    public IReadOnlyList<string> FrameworkChoices =>
        FindParameter(FrameworkParameterName)?.Choices.Select(c => c.Value).ToList() ?? [];

    /// <summary>
    /// Whether this template's name will become a type name, and so has to be a valid identifier
    /// rather than merely a valid file name.
    ///
    /// <para>Derived rather than hard-coded: a template that has a language and whose own default
    /// name is an identifier produces a type (Class -&gt; "Class1", API Controller -&gt;
    /// "ValueController"), while the config-file templates have neither (EditorConfig file has no
    /// language, gitignore's default name starts with a dot).</para>
    /// </summary>
    public bool NameBecomesTypeName =>
        !string.IsNullOrWhiteSpace(Language) &&
        DefaultName is { Length: > 0 } defaultName &&
        (char.IsLetter(defaultName[0]) || defaultName[0] == '_') &&
        defaultName.All(c => char.IsLetterOrDigit(c) || c == '_');

    public const string FrameworkParameterName = "Framework";

    public const string NamespaceParameterName = "namespace";
}
