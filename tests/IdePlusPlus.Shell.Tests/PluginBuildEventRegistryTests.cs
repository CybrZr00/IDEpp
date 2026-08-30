using IdePlusPlus.PluginContracts;
using IdePlusPlus.Shell.ToolPanes;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Shell.Tests;

public class PluginBuildEventRegistryTests
{
    private static BuildEventContext Context(BuildEventKind kind = BuildEventKind.BeforeBuild, int? exitCode = null) =>
        new(kind, @"C:\repo\App.slnx", "Debug", exitCode);

    [Fact]
    public async Task DispatchAsync_RunsOnlyHandlersForThatEvent()
    {
        var registry = new PluginBuildEventRegistry(new OutputPaneViewModel());
        var ran = new List<string>();

        registry.Register("A", BuildEventKind.BeforeBuild, (_, _) => { ran.Add("before"); return Task.CompletedTask; });
        registry.Register("B", BuildEventKind.AfterBuild, (_, _) => { ran.Add("after"); return Task.CompletedTask; });

        await registry.DispatchAsync(Context(BuildEventKind.BeforeBuild));

        ran.ShouldBe(["before"]);
    }

    [Fact]
    public async Task DispatchAsync_RunsHandlersInRegistrationOrder()
    {
        var registry = new PluginBuildEventRegistry(new OutputPaneViewModel());
        var ran = new List<string>();

        registry.Register("first", BuildEventKind.BeforeBuild, (_, _) => { ran.Add("first"); return Task.CompletedTask; });
        registry.Register("second", BuildEventKind.BeforeBuild, (_, _) => { ran.Add("second"); return Task.CompletedTask; });

        await registry.DispatchAsync(Context());

        ran.ShouldBe(["first", "second"]);
    }

    [Fact]
    public async Task DispatchAsync_PassesTheContextThrough()
    {
        var registry = new PluginBuildEventRegistry(new OutputPaneViewModel());
        BuildEventContext? seen = null;

        registry.Register("A", BuildEventKind.AfterBuild, (context, _) => { seen = context; return Task.CompletedTask; });

        await registry.DispatchAsync(Context(BuildEventKind.AfterBuild, exitCode: 0));

        seen.ShouldNotBeNull();
        seen.Kind.ShouldBe(BuildEventKind.AfterBuild);
        seen.Configuration.ShouldBe("Debug");
        seen.ExitCode.ShouldBe(0);
    }

    /// <summary>A broken plugin must not be able to make the IDE unable to build.</summary>
    [Fact]
    public async Task DispatchAsync_AThrowingHandlerIsLoggedAndTheRestStillRun()
    {
        var output = new OutputPaneViewModel();
        var registry = new PluginBuildEventRegistry(output);
        var laterRan = false;

        registry.Register("Broken", BuildEventKind.BeforeBuild, (_, _) => throw new InvalidOperationException("boom"));
        registry.Register("Fine", BuildEventKind.BeforeBuild, (_, _) => { laterRan = true; return Task.CompletedTask; });

        await Should.NotThrowAsync(() => registry.DispatchAsync(Context()));

        laterRan.ShouldBeTrue();
        output.Text.ShouldContain("Broken");
        output.Text.ShouldContain("boom");
    }

    [Fact]
    public async Task DispatchAsync_AFaultedTaskIsLoggedRatherThanThrown()
    {
        var output = new OutputPaneViewModel();
        var registry = new PluginBuildEventRegistry(output);

        registry.Register("Broken", BuildEventKind.BeforeBuild, (_, _) => Task.FromException(new InvalidOperationException("async boom")));

        await Should.NotThrowAsync(() => registry.DispatchAsync(Context()));

        output.Text.ShouldContain("async boom");
    }

    /// <summary>Including a handler that ignores its cancellation token - the build must move on
    /// regardless, which is why dispatch waits on the token rather than on the handler.</summary>
    [Fact]
    public async Task DispatchAsync_AHangingHandlerIsAbandonedAndTheRestStillRun()
    {
        var output = new OutputPaneViewModel();
        var registry = new PluginBuildEventRegistry(output, TimeSpan.FromMilliseconds(200));
        var laterRan = false;

        registry.Register("Hung", BuildEventKind.BeforeBuild, (_, _) => new TaskCompletionSource().Task);
        registry.Register("Fine", BuildEventKind.BeforeBuild, (_, _) => { laterRan = true; return Task.CompletedTask; });

        await Should.NotThrowAsync(() => registry.DispatchAsync(Context()));

        laterRan.ShouldBeTrue();
        output.Text.ShouldContain("Hung");
        output.Text.ShouldContain("did not finish in time");
    }

    [Fact]
    public async Task DispatchAsync_NoHandlers_IsANoOp() =>
        await Should.NotThrowAsync(() => new PluginBuildEventRegistry(new OutputPaneViewModel()).DispatchAsync(Context()));
}
