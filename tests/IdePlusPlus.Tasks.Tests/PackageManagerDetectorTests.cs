using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class PackageManagerDetectorTests
{
    [Theory]
    [InlineData("pnpm@8.6.0", NodePackageManager.Pnpm)]
    [InlineData("yarn@3.2.1", NodePackageManager.Yarn)]
    [InlineData("npm@10.2.0", NodePackageManager.Npm)]
    [InlineData("PNPM", NodePackageManager.Pnpm)]
    public void Detect_PackageManagerField_Wins(string field, NodePackageManager expected)
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WritePackageJson("{}");

        // A contradicting lockfile is deliberately present: the repo declaring a manager explicitly
        // is a stronger signal than whatever lockfile happens to be checked in.
        directory.WriteFile("yarn.lock");

        PackageManagerDetector.Detect(manifestPath, field).ShouldBe(expected);
    }

    [Fact]
    public void Detect_UnrecognizedPackageManagerField_FallsBackToLockfile()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WritePackageJson("{}");
        directory.WriteFile("pnpm-lock.yaml");

        PackageManagerDetector.Detect(manifestPath, "bun@1.0.0").ShouldBe(NodePackageManager.Pnpm);
    }

    [Theory]
    [InlineData("pnpm-lock.yaml", NodePackageManager.Pnpm)]
    [InlineData("yarn.lock", NodePackageManager.Yarn)]
    [InlineData("package-lock.json", NodePackageManager.Npm)]
    public void Detect_LockfileBesideManifest(string lockFileName, NodePackageManager expected)
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WritePackageJson("{}");
        directory.WriteFile(lockFileName);

        PackageManagerDetector.Detect(manifestPath, packageManagerField: null).ShouldBe(expected);
    }

    /// <summary>The workspace case: in a pnpm monorepo only the root has a lockfile, and a package
    /// several directories down still has to run with pnpm.</summary>
    [Fact]
    public void Detect_WalksUpToTheNearestLockfile()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WriteFile("packages/web/package.json", "{}");
        directory.WriteFile("pnpm-lock.yaml");

        PackageManagerDetector.Detect(manifestPath, packageManagerField: null).ShouldBe(NodePackageManager.Pnpm);
    }

    [Fact]
    public void Detect_NearerLockfileBeatsTheRootOne()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WriteFile("packages/web/package.json", "{}");
        directory.WriteFile("packages/web/yarn.lock");
        directory.WriteFile("pnpm-lock.yaml");

        PackageManagerDetector.Detect(manifestPath, packageManagerField: null).ShouldBe(NodePackageManager.Yarn);
    }

    [Fact]
    public void Detect_NoFieldAndNoLockfile_DefaultsToNpm()
    {
        using var directory = new TempDirectory();
        var manifestPath = directory.WritePackageJson("{}");

        PackageManagerDetector.Detect(manifestPath, packageManagerField: null).ShouldBe(NodePackageManager.Npm);
    }
}
