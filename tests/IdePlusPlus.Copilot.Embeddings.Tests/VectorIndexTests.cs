using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Embeddings.Tests;

public class VectorIndexTests
{
    [Fact]
    public void Search_ReturnsClosestChunkFirst()
    {
        var index = new VectorIndex();
        index.Add(new CodeChunk("A.cs", 1, 1, "a") { Embedding = [1, 0, 0] });
        index.Add(new CodeChunk("B.cs", 1, 1, "b") { Embedding = [0, 1, 0] });
        index.Add(new CodeChunk("C.cs", 1, 1, "c") { Embedding = [0.9f, 0.1f, 0] });

        var results = index.Search([1, 0, 0], topK: 2);

        results.Count.ShouldBe(2);
        results[0].FilePath.ShouldBe("A.cs");
        results[1].FilePath.ShouldBe("C.cs");
    }

    [Fact]
    public void Search_IgnoresChunksWithoutEmbeddings()
    {
        var index = new VectorIndex();
        index.Add(new CodeChunk("NoEmbedding.cs", 1, 1, "x"));
        index.Add(new CodeChunk("HasEmbedding.cs", 1, 1, "y") { Embedding = [1, 0] });

        var results = index.Search([1, 0], topK: 5);

        results.Count.ShouldBe(1);
        results[0].FilePath.ShouldBe("HasEmbedding.cs");
    }

    [Fact]
    public void SaveToDisk_ThenLoadFromDisk_RoundTripsChunksAndEmbeddings()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vector-index-test-{Guid.NewGuid()}.json");
        try
        {
            var original = new VectorIndex();
            original.Add(new CodeChunk("A.cs", 1, 10, "hello") { Embedding = [0.1f, 0.2f, 0.3f] });
            original.SaveToDisk(path);

            var loaded = new VectorIndex();
            loaded.LoadFromDisk(path);

            loaded.Count.ShouldBe(1);
            var results = loaded.Search([0.1f, 0.2f, 0.3f], topK: 1);
            results[0].FilePath.ShouldBe("A.cs");
            results[0].Text.ShouldBe("hello");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromDisk_MissingFile_LeavesIndexEmpty()
    {
        var index = new VectorIndex();
        index.LoadFromDisk(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid()}.json"));
        index.Count.ShouldBe(0);
    }
}
