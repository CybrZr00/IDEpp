using System.Text.Json.Nodes;
using IdePlusPlus.Copilot.Core.Team;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using IdePlusPlus.Shell.Team;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

/// <summary>
/// The pipeline's file-ownership rules in the team dispatcher, against a real git repository: an owner
/// may edit only its own files, what it commits is checked again at merge time (a shell command can
/// write anywhere), a colleague's waiting request comes first, and none of it touches a dispatcher that
/// was built without the pipeline.
/// </summary>
public sealed class TeamToolDispatcherOwnershipTests : IDisposable
{
    private static readonly TeamRole Owner = PipelineRoles.Owner;

    private readonly TeamRepositoryFixture _repo = new();

    public void Dispose() => _repo.Dispose();

    private static JsonNode Args(object values) => JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(values))!;

    private static OwnershipMap Map(params (string Path, string Owner)[] entries) =>
        OwnershipMap.FromEntries(entries.Select(e => KeyValuePair.Create(e.Path, e.Owner)));

    private static TeamPipelineHooks Hooks(OwnershipMap map, Func<string, string?>? guard = null) => new(() => map, guard);

    private Task<(TeamToolDispatcher Dispatcher, TeamWorkerContext Context, TeamWorkerState State)> OwnerAsync(
        int number,
        TeamPipelineHooks? hooks,
        Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>>? runCommand = null) =>
        _repo.CreateWorkerAsync(number, runCommand, role: Owner, pipeline: hooks);

    private static Task<(bool IsError, string Text)> Edit(TeamToolDispatcher dispatcher, string file, string text) =>
        dispatcher.ExecuteAsync(TeamTools.EditFileToolName, Args(new { file, mode = "whole_file", new_text = text }), CancellationToken.None);

    private static Task<(bool IsError, string Text)> Run(TeamToolDispatcher dispatcher, string tool, object? arguments = null) =>
        dispatcher.ExecuteAsync(tool, arguments is null ? null : Args(arguments), CancellationToken.None);

    private static Task<(bool IsError, string Text)> Commit(TeamToolDispatcher dispatcher, string message = "work") =>
        Run(dispatcher, TeamTools.CommitToolName, new { message });

    private string Show(string path) => TeamRepositoryFixture.Git(_repo.RepositoryRoot, "show", $"{_repo.TeamBranch}:{path}");

    // --- edit_file ---

    [Fact]
    public async Task AnOwnerEditsItsOwnFile_AndMergesIt()
    {
        var (b1, context, _) = await OwnerAsync(1, Hooks(Map(("Lib/Class1.cs", "b1"))));

        var (editError, edit) = await Edit(b1, "Lib/Class1.cs", "public class Class1 { public int Value => 2; }\n");
        editError.ShouldBeFalse(edit);
        (await Commit(b1)).IsError.ShouldBeFalse();
        var (mergeError, merged) = await Run(b1, TeamTools.MergeToolName);

        mergeError.ShouldBeFalse(merged);
        Show("Lib/Class1.cs").ShouldContain("Value => 2");
        File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldContain("Value => 2");
    }

    [Fact]
    public async Task AFileOwnedBySomeoneElseCannotBeEdited_TheRefusalNamesTheOwnerAndTheWayToAsk()
    {
        var (b1, context, _) = await OwnerAsync(1, Hooks(Map(("Lib/Class1.cs", "b2"), ("Lib/Mine.cs", "b1"))));
        var before = File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs"));

        var (isError, text) = await Edit(b1, "Lib/Class1.cs", "stolen");

        isError.ShouldBeTrue();
        text.ShouldContain("owned by b2");
        text.ShouldContain("request_change");
        text.ShouldContain("await_change");
        File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldBe(before);
    }

    [Fact]
    public async Task AFileNobodyOwnsCannotBeCreated_AndTheRefusalListsWhatTheOwnerDoesOwn()
    {
        var (b1, context, _) = await OwnerAsync(1, Hooks(Map(("Lib/Class1.cs", "b1"), ("Lib/Mine.cs", "b1"))));

        var (isError, text) = await Edit(b1, "Lib/Surprise.cs", "class Surprise { }");

        isError.ShouldBeTrue();
        text.ShouldContain("not owned by anyone");
        text.ShouldContain("Lib/Class1.cs");
        text.ShouldContain("Lib/Mine.cs");
        text.ShouldContain("claim_new_file");
        File.Exists(Path.Combine(context.WorktreeRoot, "Lib", "Surprise.cs")).ShouldBeFalse();
    }

    [Fact]
    public async Task ANewFileThatTheDesignGaveToThisOwnerMayBeCreated()
    {
        var (b1, context, _) = await OwnerAsync(1, Hooks(Map(("Lib/Mine.cs", "b1"))));

        var (isError, text) = await Edit(b1, "Lib/Mine.cs", "public class Mine { }\n");

        isError.ShouldBeFalse(text);
        File.Exists(Path.Combine(context.WorktreeRoot, "Lib", "Mine.cs")).ShouldBeTrue();
    }

    [Theory]
    [InlineData("Lib/Class1.cs")]
    [InlineData("LIB/CLASS1.CS")]
    [InlineData(@"Lib\Class1.cs")]
    [InlineData("./Lib/Class1.cs")]
    [InlineData("Lib/../Lib/Class1.cs")]
    public async Task OwnershipDoesNotCareHowAPathIsSpelled(string spelling)
    {
        var (b1, _, _) = await OwnerAsync(1, Hooks(Map(("Lib/Class1.cs", "b1"))));

        var (isError, text) = await Edit(b1, spelling, "public class Class1 { }\n");

        isError.ShouldBeFalse(text);
    }

    [Theory]
    [InlineData("LIB/CLASS1.CS")]
    [InlineData(@"Lib\Class1.cs")]
    [InlineData("./Lib/Class1.cs")]
    [InlineData("Lib/../Lib/Class1.cs")]
    public async Task ADifferentSpellingOfSomeoneElsesFileIsStillTheirs(string spelling)
    {
        var (b1, _, _) = await OwnerAsync(1, Hooks(Map(("Lib/Class1.cs", "b2"))));

        var (isError, text) = await Edit(b1, spelling, "stolen");

        isError.ShouldBeTrue();
        text.ShouldContain("owned by b2");
    }

    [Fact]
    public async Task AnOwnerWithNoRulesToCheckAgainstMayEditNothing()
    {
        var (b1, _, _) = await OwnerAsync(1, hooks: null);

        var (isError, text) = await Edit(b1, "Lib/Class1.cs", "anything");

        isError.ShouldBeTrue();
        text.ShouldContain("no file ownership set up");
    }

    [Fact]
    public async Task TheMapIsReadOnEveryCall_SoAFileClaimedLaterBecomesTheOwners()
    {
        var map = OwnershipMap.FromEntries([KeyValuePair.Create("Lib/Mine.cs", "b1")]);
        var (b1, _, _) = await OwnerAsync(1, new TeamPipelineHooks(() => map));

        (await Edit(b1, "Lib/Extra.cs", "class Extra { }\n")).IsError.ShouldBeTrue();

        map = map.TryClaim("b1", "Lib/Extra.cs").Map!;

        (await Edit(b1, "Lib/Extra.cs", "class Extra { }\n")).IsError.ShouldBeFalse();
    }

    // --- The merge-time check ---

    /// <summary>A "shell command" that writes where it likes - what run_command can do and edit_file cannot.</summary>
    private static Func<string, string, string, CancellationToken, Task<(bool IsError, string Text)>> Writes(
        Action<string> write) =>
        (_, _, workingDirectory, _) =>
        {
            write(workingDirectory);
            return Task.FromResult((false, "ok"));
        };

    [Fact]
    public async Task AShellCommandThatWritesAnotherOwnersFile_IsCaughtAtMerge_AndPutBack()
    {
        var map = Map(("Lib/Class1.cs", "b2"), ("Lib/Mine.cs", "b1"));
        var runCommand = Writes(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "Lib", "Class1.cs"), "public class Class1 { public int Value => 99; }\n");
            File.WriteAllText(Path.Combine(dir, "Lib", "Sneaky.cs"), "public class Sneaky { }\n");
            File.WriteAllText(Path.Combine(dir, "Lib", "Mine.cs"), "public class Mine { }\n");
        });
        var (b1, context, _) = await OwnerAsync(1, Hooks(map), runCommand);
        var teamBefore = TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", _repo.TeamBranch);
        var class1Before = File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs"));

        (await Run(b1, "run_command", new { command = "write files", reason = "test" })).IsError.ShouldBeFalse();
        (await Commit(b1)).IsError.ShouldBeFalse();
        var (isError, text) = await Run(b1, TeamTools.MergeToolName);

        isError.ShouldBeTrue();
        text.ShouldStartWith("Not merged");
        text.ShouldContain("Lib/Class1.cs (owned by b2)");
        text.ShouldContain("Lib/Sneaky.cs (not owned)");
        text.ShouldNotContain("Lib/Mine.cs");
        text.ShouldContain("committed that");
        text.ShouldContain("request_change");

        // The team branch is untouched; the worktree has the offenders back and its own file kept.
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "rev-parse", _repo.TeamBranch).ShouldBe(teamBefore);
        File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldBe(class1Before);
        File.Exists(Path.Combine(context.WorktreeRoot, "Lib", "Sneaky.cs")).ShouldBeFalse();
        File.Exists(Path.Combine(context.WorktreeRoot, "Lib", "Mine.cs")).ShouldBeTrue();
        TeamRepositoryFixture.Git(context.WorktreeRoot, "status", "--porcelain").ShouldBeEmpty();

        // The corrective commit is on the branch, so the very next team_merge goes through.
        var (secondError, second) = await Run(b1, TeamTools.MergeToolName);
        secondError.ShouldBeFalse(second);
        Show("Lib/Mine.cs").ShouldContain("class Mine");
        Show("Lib/Class1.cs").ShouldContain("Value => 1;");
        TeamRepositoryFixture.Git(_repo.RepositoryRoot, "ls-tree", "-r", "--name-only", _repo.TeamBranch).ShouldNotContain("Sneaky");
    }

    [Fact]
    public async Task AShellCommandThatDeletesAnotherOwnersFile_HasItRestored()
    {
        var map = Map(("Lib/Class1.cs", "b2"), ("Lib/Mine.cs", "b1"));
        var runCommand = Writes(dir =>
        {
            File.Delete(Path.Combine(dir, "Lib", "Class1.cs"));
            File.WriteAllText(Path.Combine(dir, "Lib", "Mine.cs"), "public class Mine { }\n");
        });
        var (b1, context, _) = await OwnerAsync(1, Hooks(map), runCommand);

        await Run(b1, "run_command", new { command = "delete", reason = "test" });
        await Commit(b1);
        var (isError, text) = await Run(b1, TeamTools.MergeToolName);

        isError.ShouldBeTrue();
        text.ShouldContain("Lib/Class1.cs (owned by b2)");
        File.Exists(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldBeTrue();
        (await Run(b1, TeamTools.MergeToolName)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task AFileOutsideTheSolutionDirectoryCannotBeOwned_SoChangingOneIsCaught()
    {
        // The repository root's own files (here .gitignore) are inside this fixture's solution directory,
        // so own everything but one of them: an unowned file is an offender wherever it is.
        var map = Map(("Lib/Mine.cs", "b1"));
        var runCommand = Writes(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "Lib", "Mine.cs"), "public class Mine { }\n");
            File.AppendAllText(Path.Combine(dir, ".gitignore"), "extra/\n");
        });
        var (b1, _, _) = await OwnerAsync(1, Hooks(map), runCommand);

        await Run(b1, "run_command", new { command = "write", reason = "test" });
        await Commit(b1);
        var (isError, text) = await Run(b1, TeamTools.MergeToolName);

        isError.ShouldBeTrue();
        text.ShouldContain(".gitignore (not owned)");
    }

    [Fact]
    public async Task AnOwnerThatOnlyChangedItsOwnFiles_IsNotTouchedByTheCheck()
    {
        var map = Map(("Lib/Class1.cs", "b1"), ("Lib/Mine.cs", "b1"));
        var runCommand = Writes(dir => File.WriteAllText(Path.Combine(dir, "Lib", "Mine.cs"), "public class Mine { }\n"));
        var (b1, _, _) = await OwnerAsync(1, Hooks(map), runCommand);

        await Run(b1, "run_command", new { command = "write", reason = "test" });
        await Edit(b1, "Lib/Class1.cs", "public class Class1 { public int Value => 5; }\n");
        await Commit(b1);
        var (isError, text) = await Run(b1, TeamTools.MergeToolName);

        isError.ShouldBeFalse(text);
        text.ShouldNotContain("Not merged");
        Show("Lib/Mine.cs").ShouldContain("class Mine");
    }

    // --- Sync conflicts ---

    [Fact]
    public async Task AConflictInAnotherOwnersFileMayBeResolved_UntilTheMergeThatCarriesItLands()
    {
        var map = Map(("Lib/Class1.cs", "b2"), ("Lib/Mine.cs", "b1"));
        var hooks = Hooks(map);
        var runCommand = Writes(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "Lib", "Class1.cs"), "public class Class1 { public int Value => 10; }\n");
            File.WriteAllText(Path.Combine(dir, "Lib", "Mine.cs"), "public class Mine { }\n");
        });
        var (b1, context, _) = await OwnerAsync(1, hooks, runCommand);
        var (b2, _, _) = await OwnerAsync(2, hooks);

        // b1 changes Class1 behind the rules' back and commits - but does not merge yet.
        await Run(b1, "run_command", new { command = "write", reason = "test" });
        await Commit(b1);

        // b2, its real owner, changes the same lines and merges first.
        (await Edit(b2, "Lib/Class1.cs", "public class Class1 { public int Value => 20; }\n")).IsError.ShouldBeFalse();
        await Commit(b2);
        (await Run(b2, TeamTools.MergeToolName)).IsError.ShouldBeFalse();

        // b1 syncs: the two edits of one line conflict, in a file b1 does not own.
        var (syncError, sync) = await Run(b1, TeamTools.SyncToolName);
        syncError.ShouldBeTrue();
        sync.ShouldContain("Lib/Class1.cs");

        // Resolving it needs an edit of that file, which is allowed now...
        var (resolveError, resolve) = await Edit(b1, "Lib/Class1.cs", "public class Class1 { public int Value => 30; }\n");
        resolveError.ShouldBeFalse(resolve);
        (await Commit(b1, "resolve")).IsError.ShouldBeFalse();

        // ...and the merge-time check lets the resolution through rather than putting it back.
        var (mergeError, merged) = await Run(b1, TeamTools.MergeToolName);
        mergeError.ShouldBeFalse(merged);
        Show("Lib/Class1.cs").ShouldContain("Value => 30");

        // Once that merge has landed the allowance is gone: Class1 is b2's again.
        var (again, text) = await Edit(b1, "Lib/Class1.cs", "stolen again");
        again.ShouldBeTrue();
        text.ShouldContain("owned by b2");
        File.ReadAllText(Path.Combine(context.WorktreeRoot, "Lib", "Class1.cs")).ShouldContain("Value => 30");
    }

    // --- Answer first ---

    [Theory]
    [InlineData(TeamTools.EditFileToolName)]
    [InlineData(TeamTools.CommitToolName)]
    [InlineData(TeamTools.MergeToolName)]
    [InlineData("run_command")]
    public async Task WhileARequestWaitsForTheOwnersAnswer_TheToolsThatChangeWorkAreRefused(string tool)
    {
        var map = Map(("Lib/Mine.cs", "b1"));
        var asked = new List<string>();
        var hooks = Hooks(map, name =>
        {
            asked.Add(name);
            return "Answer CR3 (from b2, about Lib/Mine.cs) first: respond_change accept or reject.";
        });
        var (b1, _, _) = await OwnerAsync(1, hooks, Writes(_ => { }));

        var (isError, text) = await Run(b1, tool, new { file = "Lib/Mine.cs", mode = "whole_file", new_text = "x", command = "echo", reason = "test" });

        isError.ShouldBeTrue();
        text.ShouldContain("Answer CR3");
        asked.ShouldBe([tool]);
    }

    [Theory]
    [InlineData(TeamTools.BoardReadToolName)]
    [InlineData(TeamTools.SyncToolName)]
    [InlineData("git_status")]
    [InlineData("read_file")]
    public async Task ReadingSyncingAndLookingStayOpenWhileARequestWaits(string tool)
    {
        var asked = new List<string>();
        var hooks = Hooks(Map(("Lib/Mine.cs", "b1")), name => { asked.Add(name); return "Answer first."; });
        var (b1, _, _) = await OwnerAsync(1, hooks);

        var (_, text) = await Run(b1, tool, new { path = "Lib/Class1.cs" });

        text.ShouldNotContain("Answer first.");
        asked.ShouldBeEmpty(); // Not even asked about: it is not one of the guarded tools.
    }

    [Fact]
    public async Task OnceTheRequestIsAnswered_TheGuardLetsTheOwnerThrough()
    {
        var waiting = true;
        var hooks = Hooks(Map(("Lib/Mine.cs", "b1")), _ => waiting ? "Answer CR1 first." : null);
        var (b1, _, _) = await OwnerAsync(1, hooks);

        (await Edit(b1, "Lib/Mine.cs", "public class Mine { }\n")).IsError.ShouldBeTrue();
        waiting = false;

        (await Edit(b1, "Lib/Mine.cs", "public class Mine { }\n")).IsError.ShouldBeFalse();
    }

    // --- Nothing else changes ---

    [Fact]
    public async Task ABuilderGivenPipelineHooksIsNotConstrained_OnlyOwnedFilesOnlyRolesAre()
    {
        var hooks = Hooks(Map(("Lib/Mine.cs", "b9")));
        var (builder, _, _) = await _repo.CreateWorkerAsync(1, pipeline: hooks);

        var (editError, edit) = await Edit(builder, "Lib/Class1.cs", "public class Class1 { public int Value => 3; }\n");
        editError.ShouldBeFalse(edit);
        await Commit(builder);
        var (mergeError, merged) = await Run(builder, TeamTools.MergeToolName);

        mergeError.ShouldBeFalse(merged);
        Show("Lib/Class1.cs").ShouldContain("Value => 3");
    }

    [Fact]
    public async Task ADispatcherBuiltWithoutHooks_BehavesAsItAlwaysDid()
    {
        var (worker, _, _) = await _repo.CreateWorkerAsync(1);

        (await Edit(worker, "Lib/Anything.cs", "class Anything { }\n")).IsError.ShouldBeFalse();
        await Commit(worker);
        (await Run(worker, TeamTools.MergeToolName)).IsError.ShouldBeFalse();
    }

    [Fact]
    public async Task AnOwnersToolListHasEditAndMergeButNoBoardWriteOrDone()
    {
        var (b1, _, _) = await OwnerAsync(1, Hooks(Map(("Lib/Mine.cs", "b1"))));

        var names = b1.Tools.Select(t => t.Name).ToList();

        names.ShouldContain(TeamTools.EditFileToolName);
        names.ShouldContain(TeamTools.MergeToolName);
        names.ShouldNotContain(TeamTools.BoardWriteToolName);
        names.ShouldNotContain(TeamTools.DoneToolName);
    }

    [Fact]
    public async Task TheDesigner_IsToldTheRunnerLandsItsCommit_NotGivenAWayToMergeIt()
    {
        var (d1, _, _) = await _repo.CreateWorkerAsync(1, role: PipelineRoles.Designer);

        d1.Tools.ShouldNotContain(t => t.Name == TeamTools.MergeToolName);
        d1.Tools.ShouldContain(t => t.Name == TeamTools.CommitToolName);
        var (isError, text) = await Run(d1, TeamTools.MergeToolName);

        isError.ShouldBeTrue();
        text.ShouldContain("you do not merge");
        text.ShouldContain("design_submit");
    }
}
