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

    // The case the test above could not catch, because it starts from an empty index: switching to a
    // solution that has never been indexed used to keep the previous solution's chunks, so its code
    // kept turning up in the new solution's prompts and search_codebase results.
    [Fact]
    public void LoadFromDisk_MissingFile_DropsWhatWasLoadedBefore()
    {
        var index = new VectorIndex();
        index.Add(new CodeChunk("PreviousSolution.cs", 1, 1, "old") { Embedding = [1, 0] });

        index.LoadFromDisk(Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid()}.json"));

        index.Count.ShouldBe(0);
    }

    [Fact]
    public void LoadFromDisk_CorruptFile_DropsWhatWasLoadedBefore()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vector-index-corrupt-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, "{ not an index");
            var index = new VectorIndex();
            index.Add(new CodeChunk("PreviousSolution.cs", 1, 1, "old") { Embedding = [1, 0] });

            index.LoadFromDisk(path);

            index.Count.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromDisk_DropsAnIndexBuiltByAnotherModel()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vector-index-model-{Guid.NewGuid()}.json");
        try
        {
            var original = new VectorIndex { ModelId = "lmstudio:nomic" };
            original.Add(new CodeChunk("A.cs", 1, 1, "a") { Embedding = [1, 0] });
            original.SaveToDisk(path);

            var sameModel = new VectorIndex();
            sameModel.LoadFromDisk(path, "lmstudio:nomic");
            var otherModel = new VectorIndex();
            otherModel.LoadFromDisk(path, "onnx:bge-small-en-v1.5-int8");

            sameModel.Count.ShouldBe(1);
            sameModel.ModelId.ShouldBe("lmstudio:nomic");
            otherModel.Count.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadFromDisk_TheOldBareArrayFormat_LeavesTheIndexEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vector-index-old-{Guid.NewGuid()}.json");
        try
        {
            File.WriteAllText(path, """[{"FilePath":"A.cs","StartLine":1,"EndLine":1,"Text":"a","Embedding":[1.0]}]""");

            var index = new VectorIndex();
            index.LoadFromDisk(path, "onnx:bge-small-en-v1.5-int8");

            index.Count.ShouldBe(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Search_SkipsChunksWhoseVectorsHaveADifferentLength()
    {
        var index = new VectorIndex();
        index.Add(new CodeChunk("Short.cs", 1, 1, "s") { Embedding = [1, 0] });
        index.Add(new CodeChunk("Match.cs", 1, 1, "m") { Embedding = [0, 1, 0] });

        index.Search([0, 1, 0], topK: 5).ShouldHaveSingleItem().FilePath.ShouldBe("Match.cs");
    }
}
