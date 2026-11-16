using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using IdePlusPlus.Copilot.Memory;

namespace IdePlusPlus.Copilot.Embeddings;

/// <summary>
/// Background embedding, and the vectors saved beside the transcripts so a restart does not redo it.
///
/// The client's every failure is a null (see IEmbeddingClient), and this never asks it to download a
/// model: that happens when the codebase is first indexed. After a failure the run stops and waits
/// out the retry delay before trying again, so a model that is not there costs one quick call a
/// minute rather than one per message. <see cref="ResumeEmbedding"/> cuts the wait short, for when
/// something has just made a model available.
/// </summary>
public sealed partial class TranscriptMemoryService
{
    private static readonly TimeSpan EmbedTimeout = TimeSpan.FromSeconds(30);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Passages that are waiting for a vector - their search is keyword-only until it arrives.</summary>
    public int PendingPassageCount
    {
        get
        {
            var state = _state;
            lock (state.Gate)
            {
                return state.Pending.Count;
            }
        }
    }

    /// <summary>Tries the backlog again now rather than when the retry delay is up.</summary>
    public void ResumeEmbedding()
    {
        var state = _state;
        Interlocked.Exchange(ref state.RetryAfterTicks, 0);
        SignalPump(state);
    }

    /// <summary>Completes when background embedding has nothing more it can do for now: the backlog is
    /// done, or it is waiting out a failure. For tests and for a clean shutdown.</summary>
    public async Task WhenIdleAsync()
    {
        var state = _state;
        while (true)
        {
            await state.Pump.ConfigureAwait(false);
            if (Volatile.Read(ref state.PumpRunning) == 0)
            {
                return;
            }

            // A run has been claimed but its task is not published yet.
            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    private void SignalPump(State state)
    {
        if (Interlocked.CompareExchange(ref state.PumpRunning, 1, 0) != 0)
        {
            return;
        }

        state.Pump = Task.Run(() => PumpAsync(state));
    }

    private async Task PumpAsync(State state)
    {
        try
        {
            while (!state.Cancellation.IsCancellationRequested && DateTime.UtcNow.Ticks >= Interlocked.Read(ref state.RetryAfterTicks))
            {
                EnsureVectorModel(state);

                Passage? next;
                string? model;
                lock (state.Gate)
                {
                    model = state.VectorModelId;
                    next = state.Pending.TryPeek(out var head) ? head : null;
                }

                if (next is null)
                {
                    return;
                }

                var vector = await _embeddingClient.GetEmbeddingAsync(next.Text, state.Cancellation.Token, EmbedTimeout)
                    .ConfigureAwait(false);
                if (vector is null)
                {
                    Interlocked.Exchange(ref state.RetryAfterTicks, (DateTime.UtcNow + _retryDelay).Ticks);
                    return;
                }

                lock (state.Gate)
                {
                    // The model changed while this was in flight: the vector is for the old one.
                    if (state.VectorModelId != model)
                    {
                        continue;
                    }

                    next.Vector = vector;
                    if (state.Pending.TryPeek(out var head) && ReferenceEquals(head, next))
                    {
                        state.Pending.Dequeue();
                    }

                    AppendVectorLine(state, next, model!, vector);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // A solution switch. Nothing to report: the new solution has its own run.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Interlocked.Exchange(ref state.RetryAfterTicks, (DateTime.UtcNow + _retryDelay).Ticks);
        }
        finally
        {
            Volatile.Write(ref state.PumpRunning, 0);

            // An entry recorded after the last look at the queue and before the flag was cleared would
            // otherwise wait for the next message to be noticed.
            if (!state.Cancellation.IsCancellationRequested
                && DateTime.UtcNow.Ticks >= Interlocked.Read(ref state.RetryAfterTicks)
                && PendingPassageCountOf(state) > 0)
            {
                SignalPump(state);
            }
        }
    }

    private static int PendingPassageCountOf(State state)
    {
        lock (state.Gate)
        {
            return state.Pending.Count;
        }
    }

    /// <summary>
    /// Makes the passages' vectors those of the client's current model: when the model is not the one
    /// they were last for (the first call, or a switch in Settings), drops them, reads back what was
    /// saved for the new model, and queues the passages that still have none. Vectors from another
    /// model are never compared with the question's, since they live in a different space.
    /// </summary>
    private void EnsureVectorModel(State state)
    {
        var model = _embeddingClient.ModelId;
        lock (state.Gate)
        {
            if (state.VectorModelId == model)
            {
                return;
            }

            state.VectorModelId = model;
            foreach (var passage in state.Passages)
            {
                passage.Vector = null;
            }

            LoadSavedVectorsLocked(state, model);

            state.Pending.Clear();
            foreach (var passage in state.Passages)
            {
                if (passage.Embeddable && passage.Vector is null)
                {
                    state.Pending.Enqueue(passage);
                }
            }
        }
    }

    private static void LoadSavedVectorsLocked(State state, string model)
    {
        if (state.Directory is not { } directory || !File.Exists(TranscriptStore.VectorsFilePath(directory)))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadLines(TranscriptStore.VectorsFilePath(directory)))
            {
                VectorLine? saved;
                try
                {
                    saved = JsonSerializer.Deserialize<VectorLine>(line);
                }
                catch (JsonException)
                {
                    continue; // A line cut short by a crash.
                }

                if (saved?.Model == model
                    && state.ByKey.TryGetValue($"{saved.EntryId}/{saved.Index}", out var passage)
                    && DecodeVector(saved.Vector) is { } vector)
                {
                    passage.Vector = vector;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: the passages are simply embedded again.
        }
    }

    private static void AppendVectorLine(State state, Passage passage, string model, float[] vector)
    {
        if (state.Directory is not { } directory)
        {
            return;
        }

        var line = JsonSerializer.Serialize(new VectorLine(passage.Entry.Id, passage.Index, model, EncodeVector(vector)));
        File.AppendAllText(TranscriptStore.VectorsFilePath(directory), line + "\n", Utf8NoBom);
    }

    /// <summary>Raw floats as base64: about a third of the size of the same vector written as JSON numbers.</summary>
    private static string EncodeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return Convert.ToBase64String(bytes);
    }

    private static float[]? DecodeVector(string encoded)
    {
        try
        {
            var bytes = Convert.FromBase64String(encoded);
            if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
            {
                return null;
            }

            var vector = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
            return vector;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed record VectorLine(
        [property: JsonPropertyName("e")] string EntryId,
        [property: JsonPropertyName("i")] int Index,
        [property: JsonPropertyName("m")] string Model,
        [property: JsonPropertyName("v")] string Vector);
}
