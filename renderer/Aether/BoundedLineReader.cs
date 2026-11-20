using System.Text;

namespace Aether;

internal readonly record struct BoundedLine(string? Text, bool EndOfStream, bool TooLarge)
{
    public static BoundedLine End() => new(null, true, false);
    public static BoundedLine Oversized() => new(null, false, true);
}

internal sealed class BoundedLineReader
{
    private readonly Stream _stream;
    private readonly UTF8Encoding _encoding = new(false, true);
    private readonly byte[] _buffer = new byte[4096];
    // Keep unread bytes so one read can contain multiple frames.
    private int _offset;
    private int _count;

    public BoundedLineReader(Stream stream) => _stream = stream;

    public async Task<BoundedLine> ReadAsync(int maxBytes, CancellationToken cancellationToken)
    {
        using var frame = new MemoryStream(Math.Min(maxBytes, _buffer.Length));
        bool tooLarge = false;

        while (true)
        {
            if (_offset == _count)
            {
                _count = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _offset = 0;
                if (_count == 0)
                {
                    if (frame.Length == 0 && !tooLarge)
                        return BoundedLine.End();
                    return tooLarge ? BoundedLine.Oversized() : Decode(frame);
                }
            }

            int newline = Array.IndexOf(_buffer, (byte)'\n', _offset, _count - _offset);
            int end = newline >= 0 ? newline : _count;
            for (int index = _offset; index < end; index++)
            {
                if (frame.Length >= maxBytes)
                {
                    tooLarge = true;
                    break;
                }
                frame.WriteByte(_buffer[index]);
            }

            _offset = newline >= 0 ? newline + 1 : _count;
            if (newline >= 0)
                return tooLarge ? BoundedLine.Oversized() : Decode(frame);
        }
    }

    private BoundedLine Decode(MemoryStream frame)
    {
        byte[] bytes = frame.ToArray();
        int length = bytes.Length > 0 && bytes[^1] == '\r' ? bytes.Length - 1 : bytes.Length;
        return new BoundedLine(_encoding.GetString(bytes, 0, length), false, false);
    }
}
