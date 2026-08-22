namespace IdePlusPlus.Copilot.Embeddings;

public sealed class CodebaseIndexService
{
    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".xml", ".json", ".md", ".csproj",
    };

    private readonly LmStudioEmbeddingClient _embeddingClient;
    private readonly VectorIndex _index = new();

    public CodebaseIndexService(LmStudioEmbeddingClient embeddingClient)
    {
        _embeddingClient = embeddingClient;
    }

    public bool IsIndexed => _index.Count > 0;

    public int IndexedChunkCount => _index.Count;

    public async Task IndexFilesAsync(IReadOnlyList<string> filePaths, Action<string> onProgress, CancellationToken cancellationToken)
    {
        _index.Clear();

        var indexableFiles = filePaths.Where(f => IndexableExtensions.Contains(Path.GetExtension(f))).ToList();
        var processedFiles = 0;

        foreach (var filePath in indexableFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string content;
            try
            {
                content = await File.ReadAllTextAsync(filePath, cancellationToken);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var chunk in CodeChunker.ChunkFile(filePath, content))
            {
                var embedding = await _embeddingClient.GetEmbeddingAsync(chunk.Text, cancellationToken);
                if (embedding is null)
                {
                    // First failure almost always means LM Studio isn't reachable or misconfigured -
                    // stop rather than burning through every remaining file on doomed requests.
                    onProgress($"Stopped: couldn't get an embedding (is LM Studio running with a model loaded?). Indexed {_index.Count} chunks from {processedFiles} files before stopping.");
                    return;
                }

                chunk.Embedding = embedding;
                _index.Add(chunk);
            }

            processedFiles++;
            onProgress($"Indexed {processedFiles}/{indexableFiles.Count} files ({_index.Count} chunks)...");
        }

        onProgress($"Indexing complete: {_index.Count} chunks from {processedFiles} files.");
    }

    public async Task<IReadOnlyList<CodeChunk>> SearchAsync(string query, int topK, CancellationToken cancellationToken)
    {
        var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, cancellationToken);
        return queryEmbedding is null ? [] : _index.Search(queryEmbedding, topK);
    }

    public void LoadFromDisk(string path) => _index.LoadFromDisk(path);

    public void SaveToDisk(string path) => _index.SaveToDisk(path);
}
