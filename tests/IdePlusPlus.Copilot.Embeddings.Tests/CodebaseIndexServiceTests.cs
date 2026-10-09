using System.Net;
using System.Text;
using IdePlusPlus.Infrastructure;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

/// <summary>
/// Covers what CodebaseIndexService has to guarantee across a solution switch: loading a solution
/// with no index file must not keep the previous one's, and an index run cancelled because the
/// solution changed under it must not publish anything - before, it kept adding the old solution's
/// chunks to whatever index the new solution had loaded.
/// </summary>
public sealed class CodebaseIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusIndexServiceTests", Guid.NewGuid().ToString("N"));
    private readonly StubEmbeddingHandler _handler = new();
    private readonly CodebaseIndexService _service;

    public CodebaseIndexServiceTests()
    {
        Directory.CreateDirectory(_root);
        var settings = new AppSettingsStore(Path.Combine(_root, "settings.json"));
        settings.Set(LmStudioEmbeddingClient.ModelSettingName, "test-embedding-model");
        _service = new CodebaseIndexService(new LmStudioEmbeddingClient(new HttpClient(_handler), settings));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task LoadFromDisk_WithNoIndexFile_DropsThePreviousIndex()
    {
        await _service.IndexFilesAsync([WriteFile("A.cs", "class A { }")], _ => { }, CancellationToken.None);
        _service.IsIndexed.ShouldBeTrue();

        _service.LoadFromDisk(Path.Combine(_root, "other-solution", "embeddings.json"));

        _service.IsIndexed.ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_DropsTheIndex()
    {
        await _service.IndexFilesAsync([WriteFile("A.cs", "class A { }")], _ => { }, CancellationToken.None);

        _service.Clear();

        _service.IsIndexed.ShouldBeFalse();
        (await _service.SearchAsync("A", 5, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexFilesAsync_Cancelled_KeepsTheIndexThatWasThereBefore()
    {
        await _service.IndexFilesAsync([WriteFile("Kept.cs", "class Kept { }")], _ => { }, CancellationToken.None);

        using var cancellation = new CancellationTokenSource();
        _handler.OnRequest = cancellation.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(() =>
            _service.IndexFilesAsync([WriteFile("Dropped.cs", "class Dropped { }")], _ => { }, cancellation.Token));

        _handler.OnRequest = null;
        var hits = await _service.SearchAsync("anything", 5, CancellationToken.None);
        hits.ShouldHaveSingleItem().FilePath.ShouldEndWith("Kept.cs");
    }

    [Fact]
    public async Task IndexFilesAsync_ReplacesThePreviousIndexRatherThanAddingToIt()
    {
        await _service.IndexFilesAsync([WriteFile("First.cs", "class First { }")], _ => { }, CancellationToken.None);

        await _service.IndexFilesAsync([WriteFile("Second.cs", "class Second { }")], _ => { }, CancellationToken.None);

        var hits = await _service.SearchAsync("anything", 5, CancellationToken.None);
        hits.ShouldHaveSingleItem().FilePath.ShouldEndWith("Second.cs");
    }

    [Fact]
    public async Task IndexFilesAsync_WhenTheClientCannotGetReady_KeepsTheExistingIndex_AndSaysWhy()
    {
        await _service.IndexFilesAsync([WriteFile("Kept.cs", "class Kept { }")], _ => { }, CancellationToken.None);
        var fake = new FakeEmbeddingClient { NotReadyReason = "couldn't download the embedding model" };
        var service = new CodebaseIndexService(fake);
        var messages = new List<string>();

        await service.IndexFilesAsync([WriteFile("New.cs", "class New { }")], messages.Add, CancellationToken.None);

        service.IsIndexed.ShouldBeFalse();
        messages.ShouldContain(m => m.Contains("couldn't download the embedding model"));
        (await _service.SearchAsync("anything", 5, CancellationToken.None)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SearchAsync_AfterTheEmbeddingModelChanges_FindsNothingRatherThanRankingByNoise()
    {
        var fake = new FakeEmbeddingClient { ModelId = "onnx:a" };
        var service = new CodebaseIndexService(fake);
        await service.IndexFilesAsync([WriteFile("A.cs", "class A { }")], _ => { }, CancellationToken.None);
        (await service.SearchAsync("q", 5, CancellationToken.None)).ShouldHaveSingleItem();

        fake.ModelId = "lmstudio:b";

        (await service.SearchAsync("q", 5, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task IndexFilesAsync_ChunksToTheSizeTheClientCanEmbed()
    {
        var fake = new FakeEmbeddingClient { MaxChunkLines = 20 };
        var service = new CodebaseIndexService(fake);
        var path = WriteFile("Long.cs", string.Join(Environment.NewLine, Enumerable.Range(1, 100).Select(i => $"// line {i}")));

        await service.IndexFilesAsync([path], _ => { }, CancellationToken.None);

        service.IndexedChunkCount.ShouldBeGreaterThan(5);
    }

    private sealed class FakeEmbeddingClient : IEmbeddingClient
    {
        public string ModelId { get; set; } = "fake:1";

        public int MaxChunkLines { get; set; } = 120;

        public string UnavailableHint => "fake";

        public string? NotReadyReason { get; set; }

        public Task<string?> PrepareAsync(Action<string> onProgress, CancellationToken cancellationToken) =>
            Task.FromResult(NotReadyReason);

        public Task<float[]?> GetEmbeddingAsync(string text, CancellationToken cancellationToken, TimeSpan? timeout = null, bool isQuery = false) =>
            Task.FromResult<float[]?>([1f, 0f]);
    }

    /// <summary>Answers every /embeddings request with the same one-dimensional vector - enough for
    /// every chunk to be indexed and found, which is all these tests look at.</summary>
    private sealed class StubEmbeddingHandler : HttpMessageHandler
    {
        public Action? OnRequest { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            OnRequest?.Invoke();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"embedding":[1.0]}]}""", Encoding.UTF8, "application/json"),
            });
        }
    }
}
