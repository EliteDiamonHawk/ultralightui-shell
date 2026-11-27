namespace Aether;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public int Area => IsEmpty ? 0 : checked(Width * Height);

    public PixelRect Clamp(int width, int height)
    {
        int left = Math.Clamp(X, 0, width);
        int top = Math.Clamp(Y, 0, height);
        int right = checked((int)Math.Clamp((long)X + Width, 0L, width));
        int bottom = checked((int)Math.Clamp((long)Y + Height, 0L, height));
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    public static PixelRect Union(PixelRect first, PixelRect second)
    {
        if (first.IsEmpty) return second;
        if (second.IsEmpty) return first;
        int left = Math.Min(first.X, second.X);
        int top = Math.Min(first.Y, second.Y);
        int right = Math.Max(first.X + first.Width, second.X + second.Width);
        int bottom = Math.Max(first.Y + first.Height, second.Y + second.Height);
        return new(left, top, right - left, bottom - top);
    }
}
