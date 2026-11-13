using System.Diagnostics;
using IdePlusPlus.SourceControl;
using Shouldly;
using Xunit;

namespace IdePlusPlus.SourceControl.Tests;

/// <summary>
/// A worktree has none of the folders git ignores, and a project can build from one: FamWholesale.csproj
/// copies "node_modules\bootstrap\scss\..." to its output, so a team's copy of it failed with MSB3030 on
/// files that were there in the user's own tree. These run against a real repository and real junctions,
/// because the claim that matters is how git itself treats them.
/// </summary>
public sealed class TeamIgnoredDependenciesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-ignored-{Guid.NewGuid():N}");
    private readonly string _repo;
    private readonly TeamGitWorkspace _workspace = new();

    public TeamIgnoredDependenciesTests()
    {
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        Git("init", "--initial-branch=main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test User");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a lingering handle on Windows shouldn't fail the test run.
        }
    }

    private string Git(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        return output.Trim();
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(_repo, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Commit()
    {
        Git("add", "-A");
        Git("commit", "-m", "x");
    }

    [Fact]
    public async Task FindIgnoredDependencyFolders_ReturnsEachIgnoredNodeModules_AtAnyDepth_WithForwardSlashes()
    {
        Write(".gitignore", "node_modules/\nbin/\n");
        Write("App/App.csproj", "<Project />");
        Write("App/node_modules/bootstrap/package.json", "{}");
        Write("App/Styles/node_modules/sass/package.json", "{}");
        Write("node_modules/root/package.json", "{}");
        Write("App/bin/Debug/App.dll", "x");
        Commit();

        var found = await _workspace.FindIgnoredDependencyFoldersAsync(_repo);

        found.ShouldBe(["App/Styles/node_modules", "App/node_modules", "node_modules"]);
    }

    [Fact]
    public async Task FindIgnoredDependencyFolders_LeavesOutBuildOutputAndAnythingGitDoesNotIgnore()
    {
        // Not ignored here: this repository's .gitignore only covers bin and obj, so node_modules is
        // untracked and would be part of any snapshot of the working tree already.
        Write(".gitignore", "bin/\nobj/\n");
        Write("App/node_modules/bootstrap/package.json", "{}");
        Write("App/bin/Debug/App.dll", "x");
        Write("App/obj/project.assets.json", "{}");
        Commit();

        (await _workspace.FindIgnoredDependencyFoldersAsync(_repo)).ShouldBeEmpty();
    }

    [Fact]
    public async Task FindIgnoredDependencyFolders_IgnoresATrackedFolderThatMerelyHasThatName()
    {
        Write(".gitignore", "bin/\n");
        Write("docs/node_modules/readme.md", "tracked on purpose");
        Commit();

        (await _workspace.FindIgnoredDependencyFoldersAsync(_repo)).ShouldBeEmpty();
    }

    [Fact]
    public async Task FindIgnoredDependencyFolders_OutsideARepository_IsEmpty()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;

        (await _workspace.FindIgnoredDependencyFoldersAsync(plain)).ShouldBeEmpty();
    }

    /// <summary>The safety the whole approach rests on: a junction standing where node_modules is
    /// ignored is ignored too, so a worker's `git add -A` never stages the link into the team branch.</summary>
    [Fact]
    public async Task AJunctionWhereNodeModulesIsIgnored_IsIgnoredToo_AndNeverStaged()
    {
        Write(".gitignore", "node_modules/\n");
        Write("App/App.csproj", "<Project />");
        Commit();
        var real = Directory.CreateDirectory(Path.Combine(_root, "real-node-modules")).FullName;
        File.WriteAllText(Path.Combine(real, "package.json"), "{}");
        var link = Path.Combine(_repo, "App", "node_modules");

        TeamExternalProjects.CreateLink(link, real);
        try
        {
            (await _workspace.IsIgnoredAsync(_repo, "App/node_modules")).ShouldBeTrue();
            Git("add", "-A");
            Git("status", "--porcelain").ShouldBeEmpty("the link must not show up as a change to commit");
        }
        finally
        {
            TeamExternalProjects.Unlink([link]);
        }

        File.Exists(Path.Combine(real, "package.json")).ShouldBeTrue("removing the link must leave what it pointed at");
    }

    [Fact]
    public async Task IsIgnored_IsFalseForAPathGitWouldStage()
    {
        Write(".gitignore", "node_modules/\n");
        Write("App/App.csproj", "<Project />");
        Commit();

        (await _workspace.IsIgnoredAsync(_repo, "App/App.csproj")).ShouldBeFalse();
        (await _workspace.IsIgnoredAsync(_repo, "App/generated")).ShouldBeFalse();

        // A "node_modules/" rule only matches a folder that exists, which a link always does when it is
        // checked - so the path is made real first. Backslashes are fine.
        Directory.CreateDirectory(Path.Combine(_repo, "App", "node_modules"));
        (await _workspace.IsIgnoredAsync(_repo, @"App\node_modules")).ShouldBeTrue();
    }
}
