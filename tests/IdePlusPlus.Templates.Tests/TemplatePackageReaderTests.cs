using IdePlusPlus.Templates;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Templates.Tests;

public class TemplatePackageReaderTests
{
    /// <summary>Captured verbatim from a real packages.json, including the BOM-free single-line
    /// formatting the engine writes.</summary>
    private const string RealSample = """
        {"Packages":[{"Details":{"PackageId":"Reborn.IdentityServer4.Admin.Templates","Author":"iamshen","Owners":"shen1970z","Reserved":"False","NuGetSource":"https://api.nuget.org/v3/index.json","Version":"9.0.347"},"InstallerId":"015dcbac-b4a5-49ea-94a6-061616eb60e2","LastChangeTime":"2026-02-13T18:52:12.7333222Z","MountPointUri":"C:\\Users\\markp\\.templateengine\\packages\\Reborn.IdentityServer4.Admin.Templates.9.0.347.nupkg"},{"Details":{"PackageId":"Aspire.ProjectTemplates","Author":"Microsoft","Owners":"aspire, Microsoft","Reserved":"True","NuGetSource":"https://api.nuget.org/v3/index.json","Version":"13.4.6"},"InstallerId":"015dcbac-b4a5-49ea-94a6-061616eb60e2","LastChangeTime":"2026-06-21T13:54:41.9171973Z","MountPointUri":"C:\\Users\\markp\\.templateengine\\packages\\Aspire.ProjectTemplates.13.4.6.nupkg"}]}
        """;

    [Fact]
    public void ReadsEveryPackage() =>
        TemplatePackageReader.Read(RealSample)!.Select(p => p.PackageId)
            .ShouldBe(["Reborn.IdentityServer4.Admin.Templates", "Aspire.ProjectTemplates"]);

    [Fact]
    public void ReadsTheDetails()
    {
        var package = TemplatePackageReader.Read(RealSample)!.Last();

        package.Version.ShouldBe("13.4.6");
        package.Author.ShouldBe("Microsoft");
        package.DisplayName.ShouldBe("Aspire.ProjectTemplates  13.4.6");
    }

    /// <summary>Anything under .templateengine got there via `dotnet new install` and can be removed;
    /// the SDK's own templates live under its installation directory and are the installer's.</summary>
    [Fact]
    public void PackagesInstalledByTheUserAreDistinguishedFromTheSdksOwn()
    {
        TemplatePackageReader.Read(RealSample)!.ShouldAllBe(p => p.IsUserInstalled);

        new TemplatePackage("Microsoft.DotNet.Common.ProjectTemplates", "10.0.0", "Microsoft",
            @"C:\Program Files\dotnet\templates\10.0.9\microsoft.dotnet.common.projecttemplates.nupkg")
            .IsUserInstalled.ShouldBeFalse();
    }

    [Fact]
    public void APackageWithNoMountPointIsNotAssumedRemovable() =>
        new TemplatePackage("Something", null, null, null).IsUserInstalled.ShouldBeFalse();

    [Fact]
    public void AVersionlessPackageStillHasAUsableDisplayName() =>
        new TemplatePackage("Something", null, null, null).DisplayName.ShouldBe("Something");

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"NotPackages":[]}""")]
    public void UnusableJsonReadsAsNull(string json) =>
        TemplatePackageReader.Read(json).ShouldBeNull();

    [Fact]
    public void AnEmptyPackageListReadsAsEmpty() =>
        TemplatePackageReader.Read("""{"Packages":[]}""").ShouldBeEmpty();

    [Fact]
    public void EntriesWithoutAPackageIdAreSkipped() =>
        TemplatePackageReader.Read("""{"Packages":[{"Details":{}},{"Details":{"PackageId":"Good"}}]}""")!
            .Select(p => p.PackageId).ShouldBe(["Good"]);

    /// <summary>Nothing installed is an empty list, not a failure - it is the common case.</summary>
    [Fact]
    public void AMissingFileReadsAsEmpty() =>
        TemplatePackageReader.ReadFile(Path.Combine(Path.GetTempPath(), $"no-packages-{Guid.NewGuid()}.json"))
            .ShouldBeEmpty();

    [Fact]
    public void BuildsThePathTheEngineUses() =>
        TemplatePackageReader.PathFor(@"C:\Users\someone")
            .ShouldBe(Path.Combine(@"C:\Users\someone", ".templateengine", "packages.json"));

    [Fact]
    public void ReadsTheRealFileIfThisMachineHasOne()
    {
        var path = TemplatePackageReader.PathFor(TemplateCacheLocator.CliHome);

        // Asserting only that reading it never throws and never returns null: what a developer
        // machine happens to have installed is not something to pin.
        TemplatePackageReader.ReadFile(path).ShouldNotBeNull();
    }
}
