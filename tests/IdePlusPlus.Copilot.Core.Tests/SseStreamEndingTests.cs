using System.Text;
using IdePlusPlus.Copilot.Core;
using Shouldly;
using Xunit;

namespace IdePlusPlus.Copilot.Core.Tests;

/// <summary>
/// Every way a chat stream can end. The agent loop reads "the stream stopped" as "the model finished
/// this turn", so a stream that dies, errors in-band or goes quiet has to say so - otherwise the turn
/// just ends and looks like the assistant gave up.
/// </summary>
public class SseStreamEndingTests
{
    private const string TextChunk = """data: {"choices":[{"delta":{"content":"hello"}}]}""";
    private const string StopChunk = """data: {"choices":[{"delta":{},"finish_reason":"stop"}]}""";

    private static MemoryStream Sse(params string[] lines) =>
        new(Encoding.UTF8.GetBytes(string.Join("\n\n", lines) + "\n\n"));

    private static async Task<List<ChatStreamEvent>> Read(Stream stream, TimeSpan? idleTimeout = null, CancellationToken cancellationToken = default)
    {
        var events = new List<ChatStreamEvent>();
        await foreach (var e in OpenAiCompatibleChat.ReadSseStreamAsync(
                           stream, idleTimeout ?? TimeSpan.FromSeconds(30), cancellationToken))
        {
            events.Add(e);
        }

        return events;
    }

    [Fact]
    public async Task ACompleteReply_ProducesNoError()
    {
        var events = await Read(Sse(TextChunk, StopChunk, "data: [DONE]"));

        events.OfType<StreamError>().ShouldBeEmpty();
        events.OfType<TextDelta>().Single().Text.ShouldBe("hello");
    }

    [Fact]
    public async Task ClosingAfterAFinishReason_WithoutDone_IsNotAnError()
    {
        // Some servers just close the connection once they have sent finish_reason.
        var events = await Read(Sse(TextChunk, StopChunk));

        events.OfType<StreamError>().ShouldBeEmpty();
    }

    [Fact]
    public async Task ClosingMidReply_WithNeitherFinishReasonNorDone_IsReportedAsAnError()
    {
        var events = await Read(Sse(TextChunk));

        events.OfType<StreamError>().Single().Message.ShouldContain("connection closed before the model finished");
    }

    [Fact]
    public async Task AnEmptyBody_IsReportedAsAnError()
    {
        var events = await Read(new MemoryStream());

        events.OfType<StreamError>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnInBandProviderError_IsSurfacedWithItsMessage()
    {
        // The shape OpenRouter uses when the upstream model fails after streaming has started.
        var events = await Read(Sse(
            TextChunk,
            """data: {"error":{"code":502,"message":"Upstream provider disconnected"},"choices":[{"delta":{"content":""},"finish_reason":"error"}]}"""));

        var error = events.OfType<StreamError>().ToList();
        error.Count.ShouldBe(1, "the error object explains the failure; finish_reason=error must not add a second message");
        error[0].Message.ShouldContain("Upstream provider disconnected");
        error[0].Message.ShouldContain("502");
    }

    [Fact]
    public async Task FinishReasonError_WithoutAnErrorObject_IsStillReported()
    {
        var events = await Read(Sse("""data: {"choices":[{"delta":{},"finish_reason":"error"}]}"""));

        events.OfType<StreamError>().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AStreamThatGoesQuiet_IsGivenUpOnWithAMessage_InsteadOfHangingForever()
    {
        using var stream = new HangingStream(TextChunk + "\n\n");

        var events = await Read(stream, idleTimeout: TimeSpan.FromMilliseconds(150));

        events.OfType<TextDelta>().ShouldHaveSingleItem();
        events.OfType<StreamError>().Single().Message.ShouldContain("No data was received");
    }

    [Fact]
    public async Task TheUsersOwnCancellation_IsNotTurnedIntoAnError()
    {
        using var stream = new HangingStream(TextChunk + "\n\n");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => Read(stream, TimeSpan.FromSeconds(30), cts.Token));
    }

    /// <summary>Yields its bytes, then blocks until cancelled - a connection that is open but silent.</summary>
    private sealed class HangingStream(string initial) : Stream
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(initial);
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _bytes.Length)
            {
                var count = Math.Min(buffer.Length, _bytes.Length - _position);
                _bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
