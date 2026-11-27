using System.Text;
using KhaosShell.MinimalBackend;

namespace MinimalBackend.Tests;

internal static class BoundedLineReaderTests
{
    public static void Run()
    {
        PreservesCoalescedFrames();
        HandlesChunkBoundaries();
        RecoversAfterOversizedFrame();
        BoundsNewlineFreeInput();
        HandlesEof();
        HonorsCancellation();
        RejectsInvalidUtf8AndContinues();
    }

    private static void PreservesCoalescedFrames()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("first\r\nsecond\n"));
        var reader = new BoundedLineReader(stream);

        BoundedLine first = Read(reader, 64);
        BoundedLine second = Read(reader, 64);
        BoundedLine end = Read(reader, 64);

        AssertEqual("first", first.Text);
        AssertEqual("second", second.Text);
        AssertTrue(end.EndOfStream);
    }

    private static void HandlesChunkBoundaries()
    {
        using var stream = new ChunkedStream(Encoding.UTF8.GetBytes("first\r\nsecond\n"), 1);
        var reader = new BoundedLineReader(stream);

        AssertEqual("first", Read(reader, 64).Text);
        AssertEqual("second", Read(reader, 64).Text);
        AssertTrue(Read(reader, 64).EndOfStream);
    }

    private static void RecoversAfterOversizedFrame()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65) + "\nok\n"));
        var reader = new BoundedLineReader(stream);

        AssertTrue(Read(reader, 64).TooLarge);
        AssertEqual("ok", Read(reader, 64).Text);
        AssertTrue(Read(reader, 64).EndOfStream);
    }

    private static void BoundsNewlineFreeInput()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 2 * 1024 * 1024)));
        var reader = new BoundedLineReader(stream);

        BoundedLine result = Read(reader, 64);
        AssertTrue(result.TooLarge);
        AssertTrue(Read(reader, 64).EndOfStream);
    }

    private static void HandlesEof()
    {
        using var partial = new MemoryStream(Encoding.UTF8.GetBytes("partial"));
        AssertEqual("partial", Read(new BoundedLineReader(partial), 64).Text);

        using var empty = new MemoryStream();
        AssertTrue(Read(new BoundedLineReader(empty), 64).EndOfStream);
    }

    private static void HonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new BoundedLineReader(new BlockingStream());
        Task<BoundedLine> pending = reader.ReadAsync(64, cancellation.Token);

        cancellation.Cancel();
        AssertThrows<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
    }

    private static void RejectsInvalidUtf8AndContinues()
    {
        byte[] bytes = new byte[] { (byte)'x', 0xc3, 0x28, (byte)'\n', (byte)'o', (byte)'k', (byte)'\n' };
        using var stream = new MemoryStream(bytes);
        var reader = new BoundedLineReader(stream);

        AssertThrows<DecoderFallbackException>(() => Read(reader, 64));
        AssertEqual("ok", Read(reader, 64).Text);
    }

    private static BoundedLine Read(BoundedLineReader reader, int maxBytes) =>
        reader.ReadAsync(maxBytes, CancellationToken.None).GetAwaiter().GetResult();

    private static void AssertTrue(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Expected true.");
    }

    private static void AssertEqual<T>(T expected, T? actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }

    private sealed class ChunkedStream(byte[] data, int chunkSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(chunkSize, data.Length - _position);
            if (available <= 0) return 0;
            Array.Copy(data, _position, buffer, offset, Math.Min(available, count));
            _position += Math.Min(available, count);
            return Math.Min(available, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int available = Math.Min(Math.Min(chunkSize, buffer.Length), data.Length - _position);
            if (available <= 0) return ValueTask.FromResult(0);
            data.AsMemory(_position, available).CopyTo(buffer);
            _position += available;
            return ValueTask.FromResult(available);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
