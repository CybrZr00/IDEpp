using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Memory.Tests;

public sealed class ScheduleStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-schedules-{Guid.NewGuid()}");
    private readonly ScheduleStore _store = new();

    public ScheduleStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    private static ScheduleDefinition Sample(string name = "nightly-summary") => new(
        name,
        ScheduleTriggerKind.DailyAtTime,
        IntervalHours: 24,
        DailyTimeOfDay: TimeSpan.FromHours(9),
        Prompt: "Summarize anything that changed.",
        Provider: "OpenRouter",
        Model: "anthropic/claude-sonnet-4.5",
        Enabled: true,
        LastRunUtc: null,
        LastResultSummary: null);

    [Fact]
    public void Load_NoFileYet_ReturnsEmpty()
    {
        _store.Load(_solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var lastRun = DateTimeOffset.UtcNow;
        var schedule = Sample() with { LastRunUtc = lastRun, LastResultSummary = "All good." };

        _store.Save(_solutionDirectory, [schedule]);
        var loaded = _store.Load(_solutionDirectory);

        loaded.ShouldHaveSingleItem();
        loaded[0].ShouldBe(schedule);
    }

    [Fact]
    public void Save_OverwritesWhatWasThereBefore()
    {
        _store.Save(_solutionDirectory, [Sample("first")]);
        _store.Save(_solutionDirectory, [Sample("second")]);

        _store.Load(_solutionDirectory).Select(s => s.Name).ShouldBe(["second"]);
    }

    [Fact]
    public void Load_OnCorruptJson_ReturnsEmptyRatherThanThrowing()
    {
        Directory.CreateDirectory(ProjectMemoryStore.MemoryDirectory(_solutionDirectory));
        File.WriteAllText(ScheduleStore.SchedulesFilePath(_solutionDirectory), "{ not valid json");

        _store.Load(_solutionDirectory).ShouldBeEmpty();
    }

    [Fact]
    public void SchedulesFilePath_IsUnderTheCopilotSubdirectoryOfDotIdePlusPlus()
    {
        var path = ScheduleStore.SchedulesFilePath(_solutionDirectory);

        path.ShouldBe(Path.Combine(_solutionDirectory, ".ideplusplus", "copilot", "schedules.json"));
    }

    [Fact]
    public void SaveRunLog_WritesATimestampedFileUnderTheSchedulesRunLogDirectory()
    {
        _store.SaveRunLog(_solutionDirectory, "nightly-summary", "Nothing notable changed.");

        var directory = ScheduleStore.RunLogDirectory(_solutionDirectory, "nightly-summary");
        var files = Directory.GetFiles(directory);
        files.ShouldHaveSingleItem();
        File.ReadAllText(files[0]).ShouldBe("Nothing notable changed.");
    }

    /// <summary>The schedule name is user-typed, not something a caller should have to sanitize
    /// before it's safe to build a path from - a name shaped like a path must not escape the
    /// schedules directory or otherwise break Directory.CreateDirectory.</summary>
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b\\c")]
    [InlineData("con:special*chars?")]
    public void RunLogDirectory_ForANameShapedLikeAPath_StaysUnderTheSchedulesDirectory(string name)
    {
        var schedulesRoot = Path.Combine(ProjectMemoryStore.MemoryDirectory(_solutionDirectory), "schedules");
        var directory = ScheduleStore.RunLogDirectory(_solutionDirectory, name);

        Path.GetFullPath(directory).ShouldStartWith(Path.GetFullPath(schedulesRoot) + Path.DirectorySeparatorChar);
    }
}
