namespace IdePlusPlus.Templates;

/// <summary>
/// What a template produces, from its <c>type</c> tag. <see cref="Solution"/> is not a curiosity:
/// aspire-starter, maui-multiproject and maui-blazor-web are tagged that way and emit several
/// projects *and their own solution file*, so "add this to the open solution" is not a meaningful
/// thing to do with one - see docs/project-templates-design.md.
/// </summary>
public enum TemplateKind
{
    /// <summary>No <c>type</c> tag, or one this version doesn't know. Two templates in the SDK's own
    /// cache have no type tag at all, so this is a real case rather than defensive padding.</summary>
    Unknown,
    Project,
    Item,
    Solution,
}
