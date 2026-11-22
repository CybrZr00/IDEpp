using System.Text;
using IdePlusPlus.Copilot.Core.Team.Pipeline;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

public sealed class PipelineArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"ideplusplus-phases-{Guid.NewGuid():N}");
    private readonly PipelineArtifactStore _store;

    public PipelineArtifactStoreTests()
    {
        _store = new PipelineArtifactStore(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    private sealed record Sample(string Name, int Count, SampleKind Kind = SampleKind.First, string? Note = null);

    private enum SampleKind
    {
        First,
        SecondOne,
    }

    // --- Paths ---

    [Theory]
    [InlineData("design.json", "design.json")]
    [InlineData("survey/u1.json", "survey/u1.json")]
    [InlineData(@"loop-1\verdicts\j1.json", "loop-1/verdicts/j1.json")]
    [InlineData("./design.json", "design.json")]
    [InlineData("  design.json  ", "design.json")]
    public void NormalizeRelativePath_AcceptsPlainRelativePaths(string path, string expected) =>
        PipelineArtifactStore.NormalizeRelativePath(path).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("../x.json")]
    [InlineData(@"..\x.json")]
    [InlineData("a/../../x.json")]
    [InlineData("a/./b.json")]
    [InlineData("/abs.json")]
    [InlineData(@"\abs.json")]
    [InlineData(@"C:\x.json")]
    [InlineData("a.json:stream")]
    [InlineData("a//b.json")]
    [InlineData("dir/")]
    [InlineData("name.")]
    [InlineData("a/name /b.json")] // (a trailing space on the whole path is only trimmed, not refused)
    [InlineData("a/name./b.json")]
    [InlineData("a*b.json")]
    [InlineData("a?b.json")]
    [InlineData("a|b.json")]
    [InlineData("con.json")]
    [InlineData("NUL")]
    [InlineData("survey/COM1.txt")]
    [InlineData("lpt9.log")]
    public void NormalizeRelativePath_RefusesAnythingThatIsNotAPlainRelativePath(string? path) =>
        PipelineArtifactStore.NormalizeRelativePath(path).ShouldBeNull();

    [Fact]
    public void Resolve_StaysInsideTheRunFolder_AndNeverReturnsTheFolderItself()
    {
        _store.Resolve("design.json").ShouldBe(Path.Combine(Path.GetFullPath(_root), "design.json"));
        _store.Resolve("a/b/c.json")!.ShouldStartWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar);
        _store.Resolve("..").ShouldBeNull();
        _store.Resolve(null).ShouldBeNull();
    }

    // --- JSON round trip ---

    [Fact]
    public async Task WriteJsonAsync_ThenReadJsonAsync_RoundTripsTheValue()
    {
        var sample = new Sample("alpha", 3, SampleKind.SecondOne, "note");

        await _store.WriteJsonAsync("survey/u1.json", sample);
        var read = await _store.ReadJsonAsync<Sample>("survey/u1.json");

        read.Ok.ShouldBeTrue(read.Error);
        read.Value.ShouldBe(sample);
    }

    [Fact]
    public async Task TheFileCarriesItsSchemaVersion_CamelCase_AndEnumsAsWords()
    {
        await _store.WriteJsonAsync("x.json", new Sample("alpha", 3, SampleKind.SecondOne));

        var text = await File.ReadAllTextAsync(Path.Combine(_root, "x.json"));

        text.ShouldContain("\"schemaVersion\": 1");
        text.ShouldContain("\"data\"");
        text.ShouldContain("\"name\": \"alpha\"");
        text.ShouldContain("\"kind\": \"secondOne\"");
        text.ShouldNotContain("note"); // Nulls are left out.
        text.ShouldNotStartWith("\uFEFF"); // No byte order mark.
    }

    [Fact]
    public async Task ReadJsonAsync_IsTolerantOfCasingCommentsAndTrailingCommas()
    {
        await _store.WriteTextAsync(
            "hand-edited.json",
            """
            {
              // an edit by hand
              "SchemaVersion": 1,
              "DATA": { "NAME": "alpha", "count": "7", "kind": "FIRST", },
            }
            """);

        var read = await _store.ReadJsonAsync<Sample>("hand-edited.json");

        read.Ok.ShouldBeTrue(read.Error);
        read.Value.ShouldBe(new Sample("alpha", 7));
    }

    [Fact]
    public async Task WriteJsonAsync_OverwritesAnExistingFile_AndLeavesNoTemporaryFilesBehind()
    {
        await _store.WriteJsonAsync("loop-1/findings.json", new Sample("first", 1));
        await _store.WriteJsonAsync("loop-1/findings.json", new Sample("second", 2));

        (await _store.ReadJsonAsync<Sample>("loop-1/findings.json")).Value.ShouldBe(new Sample("second", 2));
        Directory.GetFiles(Path.Combine(_root, "loop-1"), "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName).ShouldBe(["findings.json"]);
    }

    [Fact]
    public async Task ConcurrentWritesOfOneFile_AlwaysLeaveAWholeFile()
    {
        var writers = Enumerable.Range(0, 24).Select(i => _store.WriteJsonAsync("race.json", new Sample(new string('x', 5000), i)));
        await Task.WhenAll(writers);

        var read = await _store.ReadJsonAsync<Sample>("race.json");

        read.Ok.ShouldBeTrue(read.Error);
        read.Value!.Name.Length.ShouldBe(5000);
        Directory.GetFiles(_root).Select(Path.GetFileName).ShouldBe(["race.json"]);
    }

    [Fact]
    public async Task ReadJsonAsync_ReportsAMissingFile_NotAnException()
    {
        var read = await _store.ReadJsonAsync<Sample>("nope.json");

        read.Ok.ShouldBeFalse();
        read.Error!.ShouldContain("does not exist");
        read.Value.ShouldBeNull();
    }

    [Theory]
    [InlineData("this is not json")]
    [InlineData("{ \"schemaVersion\": 1, \"data\": ")]
    [InlineData("{ \"schemaVersion\": 1, \"data\": { \"name\": 5, \"count\": \"many\" } }")]
    public async Task ReadJsonAsync_ReportsAFileThatIsNotValid(string content)
    {
        await _store.WriteTextAsync("bad.json", content);

        var read = await _store.ReadJsonAsync<Sample>("bad.json");

        read.Ok.ShouldBeFalse();
        read.Error!.ShouldContain("bad.json");
    }

    [Fact]
    public async Task ReadJsonAsync_ReportsAFileWithNoData()
    {
        await _store.WriteTextAsync("nodata.json", """{ "schemaVersion": 1 }""");

        var read = await _store.ReadJsonAsync<Sample>("nodata.json");

        read.Ok.ShouldBeFalse();
        read.Error!.ShouldContain("no data");
    }

    [Fact]
    public async Task ReadJsonAsync_RefusesAFileFromANewerVersionOfTheApp()
    {
        await _store.WriteTextAsync("future.json", """{ "schemaVersion": 99, "data": { "name": "a", "count": 1 } }""");

        var read = await _store.ReadJsonAsync<Sample>("future.json");

        read.Ok.ShouldBeFalse();
        read.Error!.ShouldContain("newer");
        read.Error!.ShouldContain("99");
    }

    [Fact]
    public async Task ReadJsonAsync_AFileFromAnOlderVersionStillReads()
    {
        await _store.WriteTextAsync("old.json", """{ "schemaVersion": 0, "data": { "name": "a", "count": 1 } }""");

        (await _store.ReadJsonAsync<Sample>("old.json")).Value.ShouldBe(new Sample("a", 1));
    }

    [Fact]
    public async Task ReadJsonAsync_RefusesAnUnsafePath_WithoutTouchingTheDisk()
    {
        var read = await _store.ReadJsonAsync<Sample>("../secret.json");

        read.Ok.ShouldBeFalse();
        read.Error!.ShouldContain("not a safe path");
    }

    [Fact]
    public async Task WriteJsonAsync_RefusesAnUnsafePath_AndWritesNothingOutside()
    {
        var outside = Path.Combine(Path.GetDirectoryName(_root)!, "escaped.json");

        await Should.ThrowAsync<ArgumentException>(() => _store.WriteJsonAsync("../escaped.json", new Sample("x", 1)));
        await Should.ThrowAsync<ArgumentException>(() => _store.WriteTextAsync(@"C:\Windows\x.txt", "x"));

        File.Exists(outside).ShouldBeFalse();
    }

    [Fact]
    public async Task WriteTextAsync_RoundTripsUnicodeWithoutAByteOrderMark()
    {
        const string text = "digest: café — naïve résumé, 日本語";

        await _store.WriteTextAsync("loop-1/digest.md", text);

        (await _store.ReadTextAsync("loop-1/digest.md")).ShouldBe(text);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(_root, "loop-1", "digest.md"));
        bytes.Take(3).ShouldNotBe(new byte[] { 0xEF, 0xBB, 0xBF });
        Encoding.UTF8.GetString(bytes).ShouldBe(text);
    }

    [Fact]
    public async Task ReadTextAsync_OfAMissingFile_IsNull() => (await _store.ReadTextAsync("missing.md")).ShouldBeNull();

    [Fact]
    public async Task Exists_AndList_SeeWhatWasWritten_InAStableOrder_WithoutTemporaryFiles()
    {
        await _store.WriteTextAsync("survey/u2.json", "{}");
        await _store.WriteTextAsync("survey/u1.json", "{}");
        await _store.WriteTextAsync("design.json", "{}");
        await File.WriteAllTextAsync(Path.Combine(_root, "survey", ".u3.json.abc.tmp"), "half a write");

        _store.Exists("design.json").ShouldBeTrue();
        _store.Exists("survey/u1.json").ShouldBeTrue();
        _store.Exists("survey/u9.json").ShouldBeFalse();
        _store.Exists("../design.json").ShouldBeFalse();

        _store.List().ShouldBe(["design.json", "survey/u1.json", "survey/u2.json"]);
        _store.List("survey").ShouldBe(["survey/u1.json", "survey/u2.json"]);
        _store.List("missing-folder").ShouldBeEmpty();
        _store.List("..").ShouldBeEmpty();
    }

    // --- The allow-listed reader ---

    private PipelineArtifactReader ReaderFor(params string[] allowed) => new(_store, allowed);

    [Fact]
    public void TheReaderAllowsExactFilesAndWholeFolders_OnlyAsTheyAreSpelled()
    {
        var reader = ReaderFor("design.json", "survey/", "loop-1/digest.md");

        reader.IsAllowed("design.json").ShouldBeTrue();
        reader.IsAllowed("DESIGN.JSON").ShouldBeTrue();
        reader.IsAllowed("survey/u1.json").ShouldBeTrue();
        reader.IsAllowed(@"survey\deep\u1.json").ShouldBeTrue();
        reader.IsAllowed("loop-1/digest.md").ShouldBeTrue();

        reader.IsAllowed("design.json.bak").ShouldBeFalse(); // A file entry is that file, not a prefix.
        reader.IsAllowed("surveyor/x.json").ShouldBeFalse(); // A folder entry ends at the folder.
        reader.IsAllowed("survey").ShouldBeFalse();
        reader.IsAllowed("loop-1/verdicts/j1.json").ShouldBeFalse();
        reader.IsAllowed("roster.json").ShouldBeFalse();
    }

    [Theory]
    [InlineData("survey/../design.json")]
    [InlineData("../survey/u1.json")]
    [InlineData("survey/../../secret.txt")]
    [InlineData(@"survey\..\..\secret.txt")]
    [InlineData("/survey/u1.json")]
    [InlineData("survey/u1.json:stream")]
    [InlineData(null)]
    [InlineData("")]
    public void TheReaderRefusesPathsThatEscapeEvenWhenTheyStartInsideAnAllowedFolder(string? path) =>
        ReaderFor("survey/", "design.json").IsAllowed(path).ShouldBeFalse();

    [Fact]
    public async Task TheReaderReturnsTheText_AndExplainsARefusalByNamingWhatMayBeRead()
    {
        await _store.WriteTextAsync("survey/u1.json", "survey text");
        await _store.WriteTextAsync("roster.json", "secret roster");
        var reader = ReaderFor("survey/", "design.json");

        (await reader.ReadAsync("survey/u1.json")).ShouldBe((false, "survey text"));

        var refused = await reader.ReadAsync("roster.json");
        refused.IsError.ShouldBeTrue();
        refused.Text.ShouldContain("survey/");
        refused.Text.ShouldContain("design.json");
        refused.Text.ShouldNotContain("secret roster");

        var missing = await reader.ReadAsync("design.json");
        missing.IsError.ShouldBeTrue();
        missing.Text.ShouldContain("no artifact");
    }

    [Fact]
    public async Task TheReaderCutsALongArtifact_AndSaysHowMuchIsMissing()
    {
        await _store.WriteTextAsync("design.json", new string('a', 1000));
        var reader = new PipelineArtifactReader(_store, ["design.json"], maxChars: 100);

        var (isError, text) = await reader.ReadAsync("design.json");

        isError.ShouldBeFalse();
        text.ShouldStartWith(new string('a', 100));
        text.ShouldContain("[truncated: 900 more characters]");
    }

    [Fact]
    public async Task TheReaderWithNothingAllowedRefusesEverything()
    {
        await _store.WriteTextAsync("design.json", "x");
        var reader = ReaderFor();

        (await reader.ReadAsync("design.json")).IsError.ShouldBeTrue();
        reader.ListReadable().ShouldBeEmpty();
        reader.Allowed.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheReaderListsOnlyWhatItMayReadAndWhatExists()
    {
        await _store.WriteTextAsync("survey/u1.json", "{}");
        await _store.WriteTextAsync("survey/u2.json", "{}");
        await _store.WriteTextAsync("design.json", "{}");
        await _store.WriteTextAsync("roster.json", "{}");

        ReaderFor("survey/", "design.json", "notes/b1.md").ListReadable()
            .ShouldBe(["design.json", "survey/u1.json", "survey/u2.json"]);
    }

    [Fact]
    public void TheReaderIgnoresAnAllowListEntryThatIsNotAPath()
    {
        var reader = ReaderFor("../outside", "ok.json", "");

        reader.Allowed.ShouldBe(["ok.json"]);
    }
}
