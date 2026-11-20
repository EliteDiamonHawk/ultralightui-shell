using System.Text;

namespace KhaosShell.MinimalBackend;

internal readonly record struct BoundedLine(string? Text, bool EndOfStream, bool TooLarge);

internal sealed class BoundedLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private readonly List<byte> _line = new();
    private int _offset;
    private int _count;

    public BoundedLineReader(Stream stream) => _stream = stream;

    public async Task<BoundedLine> ReadAsync(int maxBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            int newline = _line.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                byte[] bytes = _line.Take(newline).ToArray();
                _line.RemoveRange(0, newline + 1);
                if (bytes.Length > 0 && bytes[^1] == (byte)'\r')
                    Array.Resize(ref bytes, bytes.Length - 1);
                return Decode(bytes, maxBytes);
            }

            if (_line.Count > maxBytes)
            {
                while (true)
                {
                    int index = _line.IndexOf((byte)'\n');
                    if (index >= 0)
                    {
                        _line.RemoveRange(0, index + 1);
                        break;
                    }

                    if (!await FillAsync(cancellationToken).ConfigureAwait(false))
                    {
                        return _line.Count == 0
                            ? new BoundedLine(null, true, false)
                            : new BoundedLine(null, false, true);
                    }
                }
                return new BoundedLine(null, false, true);
            }

            if (!await FillAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_line.Count == 0)
                    return new BoundedLine(null, true, false);
                byte[] bytes = _line.ToArray();
                _line.Clear();
                return Decode(bytes, maxBytes);
            }
        }
    }

    private async Task<bool> FillAsync(CancellationToken cancellationToken)
    {
        if (_offset >= _count)
        {
            _offset = 0;
            _count = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (_count == 0)
                return false;
        }

        int available = _count - _offset;
        _line.AddRange(_buffer.AsSpan(_offset, available).ToArray());
        _offset = _count;
        return true;
    }

    private static BoundedLine Decode(byte[] bytes, int maxBytes)
    {
        if (bytes.Length > maxBytes)
            return new BoundedLine(null, false, true);
        try
        {
            return new BoundedLine(new UTF8Encoding(false, true).GetString(bytes), false, false);
        }
        catch (DecoderFallbackException)
        {
            return new BoundedLine(null, false, true);
        }
    }
}
