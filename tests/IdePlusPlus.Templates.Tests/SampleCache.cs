using IdePlusPlus.Templates;

namespace IdePlusPlus.Templates.Tests;

/// <summary>
/// The captured cache excerpt every test here reads, trimmed from a stock .NET 10.0.301 install:
/// classlib in all three SDK bands, Class in C# and VB, API Controller in three bands, the two
/// NUnit item entries that carry different display names, one solution-kind template, and the one
/// entry that has no group identity at all.
/// </summary>
internal static class SampleCache
{
    public static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "templatecache-sample.json");

    public static string Json => File.ReadAllText(Path);

    public static IReadOnlyList<TemplateInfo> Templates =>
        TemplateCacheReader.Read(Json) ?? throw new InvalidOperationException("Sample cache failed to parse.");

    public static TemplateInfo Template(string identity) =>
        Templates.Single(t => t.Identity == identity);

    public static TemplateGroup Group(string groupIdentity) =>
        TemplateGrouping.Group(Templates).Single(g => g.GroupIdentity == groupIdentity);
}
