namespace Aether;

public enum GifDisposalMethod
{
    Unspecified = 0,
    DoNotDispose = 1,
    RestoreBackground = 2,
    RestorePrevious = 3
}

public sealed record GifFrame(
    PixelRect Bounds,
    ReadOnlyMemory<byte> Pixels,
    int Stride,
    GifDisposalMethod Disposal = GifDisposalMethod.DoNotDispose,
    long DelayMilliseconds = 100)
{
    public GifFrame(PixelRect bounds, ReadOnlyMemory<byte> pixels, GifDisposalMethod disposal = GifDisposalMethod.DoNotDispose,
        long delayMilliseconds = 100)
        : this(bounds, pixels, checked(bounds.Width * 4), disposal, delayMilliseconds)
    {
    }
}

public readonly record struct GifCompositionResult(int FrameIndex, PixelRect DirtyRegion, byte[] Canvas);

/// <summary>
/// Composes GIF subframes into one persistent premultiplied-BGRA canvas.
/// Transparent source pixels leave the existing canvas unchanged.
/// </summary>
public sealed class GifCompositor
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _canvas;
    private readonly byte[] _background;
    private byte[]? _restorePrevious;
    private PixelRect _previousBounds;
    private GifDisposalMethod _previousDisposal;
    private int _frameIndex;

    public GifCompositor(int width, int height, ReadOnlySpan<byte> background = default)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (background.Length is not (0 or 4))
            throw new ArgumentException("Background must be one BGRA pixel or empty.", nameof(background));

        _width = width;
        _height = height;
        _canvas = new byte[checked(width * height * 4)];
        _background = new byte[4];
        if (background.Length == 4)
            background.CopyTo(_background);
    }

    public int Width => _width;
    public int Height => _height;
    public ReadOnlyMemory<byte> Canvas => _canvas;
    public int FrameIndex => _frameIndex;

    public GifCompositionResult Compose(GifFrame frame)
    {
        PixelRect disposalDirty = ApplyPreviousDisposal();
        PixelRect bounds = frame.Bounds.Clamp(_width, _height);
        if (bounds.IsEmpty)
            throw new ArgumentException("GIF frame is outside the canvas.", nameof(frame));
        if (frame.Stride < frame.Bounds.Width * 4 || frame.Pixels.Length < frame.Stride * frame.Bounds.Height)
            throw new ArgumentException("GIF frame pixel buffer is smaller than its declared bounds.", nameof(frame));

        if (frame.Disposal == GifDisposalMethod.RestorePrevious)
        {
            int bytes = checked(bounds.Width * bounds.Height * 4);
            _restorePrevious = EnsureBuffer(_restorePrevious, bytes);
            CopyRegion(_canvas, _width, bounds, _restorePrevious, bounds.Width * 4, new(0, 0, bounds.Width, bounds.Height));
        }

        DrawFrame(frame, bounds);
        PixelRect dirty = PixelRect.Union(disposalDirty, bounds).Clamp(_width, _height);
        _previousBounds = bounds;
        _previousDisposal = frame.Disposal;
        int index = _frameIndex++;
        return new GifCompositionResult(index, dirty, _canvas);
    }

    public PixelRect ResetLoop()
    {
        PixelRect dirty = new(0, 0, _width, _height);
        Array.Clear(_canvas);
        _previousBounds = default;
        _previousDisposal = GifDisposalMethod.DoNotDispose;
        _restorePrevious = null;
        _frameIndex = 0;
        return dirty;
    }

    private PixelRect ApplyPreviousDisposal()
    {
        if (_previousBounds.IsEmpty)
            return default;

        PixelRect dirty = _previousBounds;
        switch (_previousDisposal)
        {
            case GifDisposalMethod.RestoreBackground:
                FillRegion(_canvas, _width, _previousBounds, _background);
                break;
            case GifDisposalMethod.RestorePrevious:
                if (_restorePrevious != null)
                    CopyRegion(_restorePrevious, _previousBounds.Width, new(0, 0, _previousBounds.Width, _previousBounds.Height),
                        _canvas, _width, _previousBounds);
                break;
        }
        return dirty;
    }

    private void DrawFrame(GifFrame frame, PixelRect clipped)
    {
        int sourceX = clipped.X - frame.Bounds.X;
        int sourceY = clipped.Y - frame.Bounds.Y;
        for (int y = 0; y < clipped.Height; y++)
        {
            int sourceOffset = checked((sourceY + y) * frame.Stride + sourceX * 4);
            int targetOffset = checked((clipped.Y + y) * _width * 4 + clipped.X * 4);
            for (int x = 0; x < clipped.Width; x++)
            {
                int sourcePixel = sourceOffset + x * 4;
                if (frame.Pixels.Span[sourcePixel + 3] != 0)
                    frame.Pixels.Span.Slice(sourcePixel, 4).CopyTo(_canvas.AsSpan(targetOffset + x * 4, 4));
            }
        }
    }

    private static void FillRegion(byte[] destination, int destinationWidth, PixelRect region, byte[] pixel)
    {
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            Span<byte> row = destination.AsSpan((y * destinationWidth + region.X) * 4, region.Width * 4);
            for (int x = 0; x < row.Length; x += 4)
                pixel.CopyTo(row.Slice(x, 4));
        }
    }

    private static void CopyRegion(byte[] source, int sourceWidth, PixelRect sourceRegion,
        byte[] destination, int destinationWidth, PixelRect destinationRegion)
    {
        if (sourceRegion.Width != destinationRegion.Width || sourceRegion.Height != destinationRegion.Height)
            throw new ArgumentException("Source and destination regions must have equal dimensions.");
        int bytes = sourceRegion.Width * 4;
        for (int y = 0; y < sourceRegion.Height; y++)
        {
            ReadOnlySpan<byte> sourceRow = source.AsSpan((sourceRegion.Y + y) * sourceWidth * 4 + sourceRegion.X * 4, bytes);
            sourceRow.CopyTo(destination.AsSpan((destinationRegion.Y + y) * destinationWidth * 4 + destinationRegion.X * 4, bytes));
        }
    }

    private static byte[] EnsureBuffer(byte[]? buffer, int length) =>
        buffer != null && buffer.Length >= length ? buffer : new byte[length];
}
