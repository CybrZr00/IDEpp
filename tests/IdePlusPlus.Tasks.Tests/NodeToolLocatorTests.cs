using Shouldly;
using Xunit;

namespace IdePlusPlus.Tasks.Tests;

public class NodeToolLocatorTests
{
    private const string PathExtensions = ".COM;.EXE;.BAT;.CMD";

    /// <summary>The whole reason this class exists: npm on Windows is npm.cmd, and the bare name is
    /// what a caller would otherwise hand to ProcessStartInfo.</summary>
    [Fact]
    public void Locate_FindsCmdShimOnPath()
    {
        using var directory = new TempDirectory();
        var expected = directory.WriteFile("tools/npm.cmd");

        var located = NodeToolLocator.Locate(
            "npm", startDirectory: null, path: Path.Combine(directory.Path, "tools"), PathExtensions);

        located.ShouldBe(expected, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void Locate_IgnoresExtensionlessShellScript()
    {
        using var directory = new TempDirectory();

        // What Git for Windows installs next to npm.cmd - a shell script CreateProcess cannot start.
        directory.WriteFile("tools/npm", "#!/bin/sh");

        NodeToolLocator.Locate("npm", startDirectory: null, path: Path.Combine(directory.Path, "tools"), PathExtensions)
            .ShouldBeNull();
    }

    [Fact]
    public void Locate_SearchesPathEntriesInOrder()
    {
        using var directory = new TempDirectory();
        var first = directory.WriteFile("first/npm.cmd");
        directory.WriteFile("second/npm.cmd");

        var path = string.Join(Path.PathSeparator, Path.Combine(directory.Path, "first"), Path.Combine(directory.Path, "second"));

        NodeToolLocator.Locate("npm", startDirectory: null, path, PathExtensions).ShouldBe(first, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void Locate_TrimsQuotesFromPathEntries()
    {
        using var directory = new TempDirectory();
        var expected = directory.WriteFile("tools/npm.cmd");

        var located = NodeToolLocator.Locate(
            "npm", startDirectory: null, path: $"\"{Path.Combine(directory.Path, "tools")}\"", PathExtensions);

        located.ShouldBe(expected, StringCompareShould.IgnoreCase);
    }

    /// <summary>A repo that pins its own tooling expects its local copy to win.</summary>
    [Fact]
    public void Locate_PrefersLocalNodeModulesBinOverPath()
    {
        using var directory = new TempDirectory();
        var local = directory.WriteFile("node_modules/.bin/gulp.cmd");
        directory.WriteFile("global/gulp.cmd");

        var located = NodeToolLocator.Locate(
            "gulp", directory.Path, path: Path.Combine(directory.Path, "global"), PathExtensions);

        located.ShouldBe(local, StringCompareShould.IgnoreCase);
    }

    /// <summary>In a workspace the .bin directory is at the root, not beside the manifest.</summary>
    [Fact]
    public void Locate_WalksUpForNodeModulesBin()
    {
        using var directory = new TempDirectory();
        var local = directory.WriteFile("node_modules/.bin/gulp.cmd");

        var located = NodeToolLocator.Locate(
            "gulp", Path.Combine(directory.Path, "packages", "web"), path: null, PathExtensions);

        located.ShouldBe(local, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void Locate_NotFoundAnywhere_ReturnsNull()
    {
        using var directory = new TempDirectory();

        NodeToolLocator.Locate("npm", directory.Path, path: directory.Path, PathExtensions).ShouldBeNull();
    }

    [Fact]
    public void Locate_UnsetPathExtensions_StillFindsCmd()
    {
        using var directory = new TempDirectory();
        var expected = directory.WriteFile("tools/npm.cmd");

        NodeToolLocator.Locate("npm", startDirectory: null, path: Path.Combine(directory.Path, "tools"), pathExt: null)
            .ShouldBe(expected, StringCompareShould.IgnoreCase);
    }

    [Fact]
    public void Locate_PathExtensionsWithoutLeadingDots_AreStillHonored()
    {
        using var directory = new TempDirectory();
        var expected = directory.WriteFile("tools/npm.cmd");

        NodeToolLocator.Locate("npm", startDirectory: null, path: Path.Combine(directory.Path, "tools"), pathExt: "COM;EXE;CMD")
            .ShouldBe(expected, StringCompareShould.IgnoreCase);
    }
}
