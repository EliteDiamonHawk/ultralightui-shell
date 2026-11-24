using System.Text;

namespace KhaosShell.Nyx;

internal readonly record struct BoundedLine(string? Text, bool EndOfStream, bool TooLarge);

internal sealed class BoundedLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[4096];
    private readonly List<byte> _line = [];

    public BoundedLineReader(Stream stream) => _stream = stream;

    public async Task<BoundedLine> ReadAsync(int maxBytes, CancellationToken cancellationToken)
    {
        _line.Clear();
        while (true)
        {
            int count = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
                return _line.Count == 0 ? new(null, true, false) : Finish(maxBytes);
            for (var index = 0; index < count; index++)
            {
                byte value = _buffer[index];
                if (value == (byte)'\n')
                    return Finish(maxBytes);
                if (value != (byte)'\r')
                    _line.Add(value);
                if (_line.Count > maxBytes)
                {
                    while (true)
                    {
                        count = await _stream.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                        if (count == 0) break;
                        if (_buffer.AsSpan(0, count).Contains((byte)'\n')) break;
                    }
                    _line.Clear();
                    return new(null, false, true);
                }
            }
        }
    }

    private BoundedLine Finish(int maxBytes) =>
        _line.Count > maxBytes
            ? new(null, false, true)
            : new(Encoding.UTF8.GetString(_line.ToArray()), false, false);
}
