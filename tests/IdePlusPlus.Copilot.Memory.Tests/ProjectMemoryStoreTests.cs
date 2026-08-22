using IdePlusPlus.Copilot.Memory;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Memory.Tests;

public sealed class ProjectMemoryStoreTests : IDisposable
{
    private readonly string _solutionDirectory = Path.Combine(Path.GetTempPath(), $"ideplusplus-memory-{Guid.NewGuid()}");
    private readonly ProjectMemoryStore _store = new();

    public ProjectMemoryStoreTests() => Directory.CreateDirectory(_solutionDirectory);

    public void Dispose() => Directory.Delete(_solutionDirectory, recursive: true);

    [Fact]
    public void LoadMemory_NoFileYet_ReturnsEmptyString()
    {
        _store.LoadMemory(_solutionDirectory).ShouldBe("");
    }

    [Fact]
    public void LoadMemory_ReadsWhatWasWrittenToMemoryFilePath()
    {
        Directory.CreateDirectory(ProjectMemoryStore.MemoryDirectory(_solutionDirectory));
        File.WriteAllText(ProjectMemoryStore.MemoryFilePath(_solutionDirectory), "# Overview\nSome notes.");

        _store.LoadMemory(_solutionDirectory).ShouldBe("# Overview\nSome notes.");
    }

    [Fact]
    public void SaveSessionLog_WritesATimestampedFileUnderSessionsDirectory()
    {
        _store.SaveSessionLog(_solutionDirectory, "**User:** hi");

        var sessionsDirectory = Path.Combine(ProjectMemoryStore.MemoryDirectory(_solutionDirectory), "sessions");
        var files = Directory.GetFiles(sessionsDirectory);
        files.ShouldHaveSingleItem();
        File.ReadAllText(files[0]).ShouldBe("**User:** hi");
    }

    [Fact]
    public void MemoryFilePath_IsUnderTheCopilotSubdirectoryOfDotIdePlusPlus()
    {
        var path = ProjectMemoryStore.MemoryFilePath(_solutionDirectory);

        path.ShouldBe(Path.Combine(_solutionDirectory, ".ideplusplus", "copilot", "project-memory.md"));
    }
}
