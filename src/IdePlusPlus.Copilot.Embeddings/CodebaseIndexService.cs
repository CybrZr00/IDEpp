namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// The open solution's embeddings index.
///
/// The index is only ever replaced wholesale, never changed in place once published: a run builds
/// into its own <see cref="VectorIndex"/> and swaps it in when it finishes, and a load reads into a
/// fresh one. Searches from background threads (investigate_parallel sub-agents, scheduled runs)
/// therefore always see one complete index, and a run cancelled because the solution changed under
/// it never publishes anything - before this, a run still going during a switch kept adding the old
/// solution's chunks to the index the new solution had just loaded.
/// </summary>
public sealed class CodebaseIndexService
{
    private static readonly HashSet<string> IndexableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".xaml", ".xml", ".json", ".md", ".csproj",
    };

    private readonly LmStudioEmbeddingClient _embeddingClient;
    private volatile VectorIndex _index = new();

    public CodebaseIndexService(LmStudioEmbeddingClient embeddingClient)
    {
        _embeddingClient = embeddingClient;
    }

    public bool IsIndexed => _index.Count > 0;

    public int IndexedChunkCount => _index.Count;

    public async Task IndexFilesAsync(IReadOnlyList<string> filePaths, Action<string> onProgress, CancellationToken cancellationToken)
    {
        var index = new VectorIndex();

        var indexableFiles = filePaths.Where(f => IndexableExtensions.Contains(Path.GetExtension(f))).ToList();
        var processedFiles = 0;
        var stoppedEarly = false;

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
                    stoppedEarly = true;
                    break;
                }

                chunk.Embedding = embedding;
                index.Add(chunk);
            }

            if (stoppedEarly)
            {
                break;
            }

            processedFiles++;
            onProgress($"Indexed {processedFiles}/{indexableFiles.Count} files ({index.Count} chunks)...");
        }

        // Checked once more right before publishing: the last embedding call can complete after the
        // caller cancelled, and publishing then would put this run's files into whatever the caller
        // has moved on to.
        cancellationToken.ThrowIfCancellationRequested();
        _index = index;

        // First failure almost always means LM Studio isn't reachable or misconfigured - stopping
        // rather than burning through every remaining file on doomed requests.
        onProgress(stoppedEarly
            ? $"Stopped: couldn't get an embedding (is LM Studio running with a model loaded?). Indexed {index.Count} chunks from {processedFiles} files before stopping."
            : $"Indexing complete: {index.Count} chunks from {processedFiles} files.");
    }

    public async Task<IReadOnlyList<CodeChunk>> SearchAsync(string query, int topK, CancellationToken cancellationToken)
    {
        var index = _index;
        var queryEmbedding = await _embeddingClient.GetEmbeddingAsync(query, cancellationToken);
        return queryEmbedding is null ? [] : index.Search(queryEmbedding, topK);
    }

    /// <summary>Drops the index entirely - for a solution change, before loading the new one's.</summary>
    public void Clear() => _index = new VectorIndex();

    /// <summary>Replaces the index with the one saved at <paramref name="path"/>, or with an empty one
    /// when there is nothing (readable) there.</summary>
    public void LoadFromDisk(string path)
    {
        var index = new VectorIndex();
        index.LoadFromDisk(path);
        _index = index;
    }

    public void SaveToDisk(string path) => _index.SaveToDisk(path);
}
