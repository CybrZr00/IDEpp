using Shouldly;
using Xunit;

namespace IdePlusPlus.Build.Tests;

/// <summary>
/// Covers the guard RunExclusiveAsync exposes for build bindings. Kept apart from BuildServiceTests
/// because almost none of this needs a real `dotnet build` - the point is which caller holds the
/// guard, not what MSBuild does.
/// </summary>
public class BuildServiceExclusiveTests
{
    [Fact]
    public async Task RunExclusiveAsync_ReportsRunningForTheWholeOfTheWork()
    {
        var service = new BuildService();
        var observedInside = false;

        var result = await service.RunExclusiveAsync(async () =>
        {
            observedInside = service.IsRunning;
            await Task.Yield();
            return 7;
        });

        result.ShouldBe(7);
        observedInside.ShouldBeTrue();
        service.IsRunning.ShouldBeFalse("the guard must be released when the work finishes");
    }

    [Fact]
    public async Task RunExclusiveAsync_ReleasesTheGuardWhenTheWorkThrows()
    {
        var service = new BuildService();

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.RunExclusiveAsync<int>(() => throw new InvalidOperationException("work failed")));

        service.IsRunning.ShouldBeFalse();
    }

    /// <summary>The whole point: a concurrent caller - the copilot's build tool racing the toolbar -
    /// is refused while a bound task is still running inside the section.</summary>
    [Fact]
    public async Task RunExclusiveAsync_RefusesAnUnrelatedConcurrentCaller()
    {
        var service = new BuildService();
        var insideSection = new TaskCompletionSource();
        var releaseSection = new TaskCompletionSource();

        var held = service.RunExclusiveAsync(async () =>
        {
            insideSection.SetResult();
            await releaseSection.Task;
            return 0;
        });

        await insideSection.Task;

        await Should.ThrowAsync<InvalidOperationException>(() => service.RunExclusiveAsync(() => Task.FromResult(1)));

        releaseSection.SetResult();
        await held;
    }

    /// <summary>A build started from inside the section is the caller's own build, not a competing
    /// one, so it has to proceed rather than be refused by the guard its caller took.</summary>
    [Fact]
    public async Task RunExclusiveAsync_BuildFromInsideTheSectionIsNotRefused()
    {
        var service = new BuildService();

        var exitCode = await service.RunExclusiveAsync(() =>
            // A path that does not exist: `dotnet build` fails fast with a non-zero exit code, which
            // is all this needs - the assertion is that it ran at all rather than being refused.
            service.BuildAsync(Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid()}.csproj"), _ => { }));

        exitCode.ShouldNotBe(0);
        service.IsRunning.ShouldBeFalse();
    }

    [Fact]
    public async Task RunExclusiveAsync_NestsWithoutDeadlocking()
    {
        var service = new BuildService();

        var result = await service.RunExclusiveAsync(() => service.RunExclusiveAsync(() => Task.FromResult(42)));

        result.ShouldBe(42);
        service.IsRunning.ShouldBeFalse();
    }
}
