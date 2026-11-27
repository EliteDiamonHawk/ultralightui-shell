using Vortice.WIC;

namespace Aether;

internal sealed class NativeImageAsset : IDisposable
{
    private NativeImageAsset(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    private NativeImageAsset(GifAnimation animation)
    {
        Animation = animation;
        Width = animation.Width;
        Height = animation.Height;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[]? Pixels { get; }
    public GifAnimation? Animation { get; }
    public bool IsAnimated => Animation != null;

    public static NativeImageAsset Load(string path, ImageAssetType requestedType, IWICImagingFactory? imagingFactory = null)
    {
        if (new FileInfo(path).Length > 64L * 1024 * 1024)
            throw new InvalidDataException("Image files larger than 64 MiB are not supported by the native renderer.");
        ImageAssetType type = requestedType == ImageAssetType.Auto
            ? string.Equals(Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase)
                ? ImageAssetType.Gif : ImageAssetType.Image
            : requestedType;
        return type switch
        {
            ImageAssetType.Gif => new NativeImageAsset(GifAnimation.Load(path)),
            ImageAssetType.Image => LoadStatic(path, imagingFactory),
            _ => throw new InvalidDataException($"Unsupported image type for '{path}'.")
        };
    }

    public void Dispose() => Animation?.Dispose();

    private static NativeImageAsset LoadStatic(string path, IWICImagingFactory? imagingFactory)
    {
        using IWICImagingFactory? ownedFactory = imagingFactory == null ? new IWICImagingFactory() : null;
        IWICImagingFactory factory = imagingFactory ?? ownedFactory!;
        using IWICBitmapDecoder decoder = factory.CreateDecoderFromFileName(path, FileAccess.Read, DecodeOptions.CacheOnLoad);
        using IWICBitmapFrameDecode frame = decoder.GetFrame(0);
        using IWICFormatConverter converter = factory.CreateFormatConverter();
        converter.Initialize(frame, PixelFormat.Format32bppPBGRA);

        int width = frame.Size.Width;
        int height = frame.Size.Height;
        if (width <= 0 || height <= 0 || checked((long)width * height) > 16_777_216)
            throw new InvalidDataException("Image dimensions exceed the native renderer limit.");

        int stride = checked(width * 4);
        byte[] pixels = new byte[checked(stride * height)];
        converter.CopyPixels((uint)stride, pixels);
        return new NativeImageAsset(width, height, pixels);
    }
}
