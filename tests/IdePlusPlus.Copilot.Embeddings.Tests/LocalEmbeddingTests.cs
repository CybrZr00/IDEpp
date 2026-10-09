using System.Net;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

/// <summary>
/// The model tests use the real bge-small files, which are not in the repo. Point
/// IDEPP_EMBEDDING_MODEL_DIR at a folder holding model.onnx and vocab.txt to run them; without it they
/// return early rather than download 34 MB on every test run.
/// </summary>
public sealed class LocalEmbeddingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IdePlusPlusLocalEmbedding", Guid.NewGuid().ToString("N"));

    public LocalEmbeddingTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string? RealModelDirectory()
    {
        var dir = Environment.GetEnvironmentVariable("IDEPP_EMBEDDING_MODEL_DIR");
        return dir is not null && File.Exists(Path.Combine(dir, "model.onnx")) ? dir : null;
    }

    private static float Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => x * y).Sum();

    [Fact]
    public async Task DownloadAsync_RejectsAFileWhoseChecksumDoesNotMatch_AndLeavesNothingInstalled()
    {
        var store = new LocalEmbeddingModelStore(_root);

        await Should.ThrowAsync<InvalidDataException>(() =>
            store.DownloadAsync(new HttpClient(new FixedHandler("not a model")), _ => { }, CancellationToken.None));

        store.IsInstalled.ShouldBeFalse();
        Directory.GetFiles(_root).ShouldBeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_WhenTheDownloadFails_ReturnsAReason_InsteadOfThrowing()
    {
        var client = new OnnxEmbeddingClient(new LocalEmbeddingModelStore(_root), new HttpClient(new FixedHandler("", HttpStatusCode.NotFound)));

        var reason = await client.PrepareAsync(_ => { }, CancellationToken.None);

        reason.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetEmbeddingAsync_WithNoModelInstalled_ReturnsNull_AndDoesNotDownload()
    {
        var handler = new FixedHandler("");
        var client = new OnnxEmbeddingClient(new LocalEmbeddingModelStore(_root), new HttpClient(handler));

        (await client.GetEmbeddingAsync("q", CancellationToken.None)).ShouldBeNull();

        handler.Requests.ShouldBe(0);
    }

    [Fact]
    public async Task TheRealModel_GivesUnitLengthVectors_AndRanksRelatedCodeAboveUnrelatedCode()
    {
        var dir = RealModelDirectory();
        if (dir is null)
        {
            return;
        }

        using var client = new OnnxEmbeddingClient(new LocalEmbeddingModelStore(dir), new HttpClient());
        (await client.PrepareAsync(_ => { }, CancellationToken.None)).ShouldBeNull();

        var query = await client.GetEmbeddingAsync("how do I read a file from disk", CancellationToken.None, isQuery: true);
        var related = await client.GetEmbeddingAsync("public string Load(string path) { return File.ReadAllText(path); }", CancellationToken.None);
        var unrelated = await client.GetEmbeddingAsync("button.Background = Brushes.Red; button.Width = 200;", CancellationToken.None);

        query.ShouldNotBeNull();
        related.ShouldNotBeNull();
        unrelated.ShouldNotBeNull();
        query.Length.ShouldBe(384);
        Cosine(query, query).ShouldBe(1f, 0.001f);
        Cosine(query, related).ShouldBeGreaterThan(Cosine(query, unrelated));
    }

    [Fact]
    public async Task TheRealModel_HandlesTextLongerThanItsTokenLimit()
    {
        var dir = RealModelDirectory();
        if (dir is null)
        {
            return;
        }

        using var client = new OnnxEmbeddingClient(new LocalEmbeddingModelStore(dir), new HttpClient());
        var embedding = await client.GetEmbeddingAsync(string.Join(' ', Enumerable.Repeat("namespace Foo { class Bar { } }", 500)), CancellationToken.None);

        embedding.ShouldNotBeNull().Length.ShouldBe(384);
    }

    private sealed class FixedHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
