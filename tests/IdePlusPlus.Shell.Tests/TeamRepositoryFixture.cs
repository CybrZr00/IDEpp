using System.Diagnostics;
using IdePlusPlus.Copilot.Core;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Diff;
using IdePlusPlus.Copilot.Embeddings;
using IdePlusPlus.Editor;
using IdePlusPlus.Infrastructure;
using IdePlusPlus.Roslyn;
using IdePlusPlus.Shell.Team;
using IdePlusPlus.SourceControl;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// A scratch git repository holding a one-project solution that really builds, plus helpers to
/// stand up Ultra workers on it - real git and real `dotnet build`, because what the team tools
/// promise (a merge that cannot clobber a peer's, a team branch that keeps building) is about how
/// those two actually behave.
/// </summary>
public sealed class TeamRepositoryFixture : IDisposable
{
    public const string RunId = "t1";

    private readonly List<IDisposable> _disposables = [];
    private bool _teamBranchCreated;

    public TeamRepositoryFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), $"ideplusplus-teamtools-{Guid.NewGuid():N}");
        RepositoryRoot = Directory.CreateDirectory(Path.Combine(Root, "repo")).FullName;

        Git(RepositoryRoot, "init", "--initial-branch=main");
        Git(RepositoryRoot, "config", "user.email", "test@example.com");
        Git(RepositoryRoot, "config", "user.name", "Test User");
        Git(RepositoryRoot, "config", "commit.gpgsign", "false");
        Git(RepositoryRoot, "config", "core.autocrlf", "false");

        // Standalone: nothing above a temp directory supplies Directory.Build.props, so only this
        // builds. bin/obj are ignored as in any real repository - otherwise every build would leave
        // the worktree "dirty" and team_merge could never pass.
        File.WriteAllText(Path.Combine(RepositoryRoot, ".gitignore"), "bin/\nobj/\n");
        File.WriteAllText(Path.Combine(RepositoryRoot, "Scratch.slnx"),
            """<Solution><Project Path="Lib/Lib.csproj" /></Solution>""");
        Directory.CreateDirectory(Path.Combine(RepositoryRoot, "Lib"));
        File.WriteAllText(Path.Combine(RepositoryRoot, "Lib", "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(RepositoryRoot, "Lib", "Class1.cs"),
            "public class Class1\r\n{\r\n    public int Value => 1;\r\n}\r\n");
        Git(RepositoryRoot, "add", "-A");
        Git(RepositoryRoot, "commit", "-m", "initial");

        var fileReader = new WorkspaceFileReader();
        var index = new CodebaseIndexService(new UnusedEmbeddingClient());
        var roslyn = new RoslynWorkspaceService();
        _disposables.Add(roslyn);
        ReadOnlyTools = new ReadOnlyToolDispatcher(
            fileReader, index, roslyn, new EditApplicationService(new EditorWorkspace(Path.Combine(Root, "recent.json"))));

        Board = new TeamBoard(TimeProvider.System, TimeSpan.FromMinutes(10));
        Bus = new TeamMessageBus(TimeProvider.System, ["w1", "w2"]);
    }

    public string Root { get; }

    public string RepositoryRoot { get; }

    public TeamGitWorkspace TeamGit { get; } = new();

    public ReadOnlyToolDispatcher ReadOnlyTools { get; }

    public TeamBoard Board { get; }

    public TeamMessageBus Bus { get; }

    public SemaphoreSlim BuildSlots { get; } = new(2);

    public string TeamBranch => TeamGitWorkspace.GetTeamBranchName(RunId);

    public async Task<(TeamToolDispatcher Dispatcher, TeamWorkerContext Context, TeamWorkerState State)> CreateWorkerAsync(
        int number,
        Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? runCommand = null)
    {
        if (!_teamBranchCreated)
        {
            var head = await TeamGit.GetHeadAsync(RepositoryRoot);
            (await TeamGit.CreateBranchAsync(RepositoryRoot, TeamBranch, head)).Success.ShouldBeTrueOrThrow();
            _teamBranchCreated = true;
        }

        var name = $"w{number}";
        var worktree = Path.Combine(Root, name);
        var teamHead = (await TeamGit.ResolveRefAsync(RepositoryRoot, TeamBranch))!;
        (await TeamGit.AddWorktreeAsync(
            RepositoryRoot, worktree, TeamGitWorkspace.GetWorkerBranchName(RunId, number), teamHead))
            .Success.ShouldBeTrueOrThrow();

        var context = new TeamWorkerContext(
            name,
            RepositoryRoot,
            worktree,
            worktree,
            Path.Combine(worktree, "Scratch.slnx"),
            [],
            TeamBranch,
            $"Ultra {name}");
        var state = new TeamWorkerState(name);
        var dispatcher = new TeamToolDispatcher(
            context, state, Board, Bus, ReadOnlyTools, new GitService(), TeamGit, BuildSlots, "Debug", runCommand);
        return (dispatcher, context, state);
    }

    public static string Git(string directory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
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
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {error}");
        }

        return output.Trim();
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        try
        {
            Git(RepositoryRoot, "worktree", "prune");
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort - a build server still holding obj/ must not fail the test.
        }
    }

    /// <summary>search_codebase is never called by these tests; this only satisfies the constructor.</summary>
    private sealed class UnusedEmbeddingClient : IEmbeddingClient
    {
        public string ModelId => "unused";

        public int MaxChunkLines => 40;

        public string UnavailableHint => "unused in tests";

        public Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken) =>
            Task.FromResult<string?>("unused in tests");

        public Task<float[]?> GetEmbeddingAsync(
            string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false) =>
            Task.FromResult<float[]?>(null);
    }
}

internal static class GitResultAssertions
{
    public static void ShouldBeTrueOrThrow(this bool success)
    {
        if (!success)
        {
            throw new InvalidOperationException("git setup step failed");
        }
    }
}
