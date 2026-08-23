namespace IdePlusPlus.ProjectSystem;

/// <summary>
/// Best-effort test-project detection, reusing SolutionLoader's already-evaluated MSBuild Project
/// object so this adds zero extra MSBuild evaluations. Checks the SDK's automatic $(IsTestProject)
/// property first (set once Microsoft.NET.Test.Sdk is imported); falls back to scanning
/// PackageReference items for a known test-framework package, for older SDKs that predate the
/// automatic property.
/// </summary>
public static class TestProjectDetector
{
    private static readonly string[] TestFrameworkPackageNames =
    [
        "xunit",
        "nunit",
        "MSTest.TestFramework",
        "Microsoft.NET.Test.Sdk",
    ];

    public static bool IsTestProject(Microsoft.Build.Evaluation.Project evaluated)
    {
        var isTestProjectProperty = evaluated.GetPropertyValue("IsTestProject");
        if (string.Equals(isTestProjectProperty, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return evaluated.GetItems("PackageReference")
            .Any(item => TestFrameworkPackageNames.Any(name =>
                item.EvaluatedInclude.Contains(name, StringComparison.OrdinalIgnoreCase)));
    }
}
