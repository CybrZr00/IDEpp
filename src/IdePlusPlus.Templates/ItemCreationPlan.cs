namespace IdePlusPlus.Templates;

/// <summary>
/// How one item template gets the right namespace - which of the two routes it needs, decided from
/// the template rather than from a list of names.
/// </summary>
/// <param name="BoundNamespace">What the template will have written if it wasn't told otherwise:
/// the project's root namespace, since that is what <c>msbuild:RootNamespace</c> resolves to.</param>
/// <param name="CorrectAfterCreation">True for the templates that declare no <c>namespace</c>
/// parameter, where the only way to get the folder in is to rewrite what was emitted.</param>
public sealed record ItemCreationPlan(
    TemplateCreationRequest Request,
    string DesiredNamespace,
    string BoundNamespace,
    bool CorrectAfterCreation)
{
    /// <summary>
    /// Builds the plan for adding <paramref name="name"/> to <paramref name="targetDirectory"/>.
    /// </summary>
    /// <param name="rootNamespace">The target project's <c>$(RootNamespace)</c>. The SDK defaults it
    /// to the project name, so it is always set in practice.</param>
    public static ItemCreationPlan For(
        TemplateInfo template,
        string name,
        string targetDirectory,
        string projectDirectory,
        string rootNamespace,
        string? language = null,
        string? projectPath = null,
        IReadOnlyDictionary<string, string?>? parameters = null)
    {
        var desired = NamespaceResolver.Resolve(rootNamespace, projectDirectory, targetDirectory);
        var declaresParameter = template.DeclaresNamespaceParameter;

        return new ItemCreationPlan(
            new TemplateCreationRequest(
                template.ShortName,
                name,
                targetDirectory,
                Language: language,

                // Passed only where it is accepted: an unrecognized option is a hard CLI error, and
                // class/interface/enum/record/struct genuinely do not take one.
                Namespace: declaresParameter ? desired : null,
                ProjectPath: projectPath,
                Parameters: parameters),
            desired,
            rootNamespace,
            CorrectAfterCreation: !declaresParameter && desired != rootNamespace);
    }
}
