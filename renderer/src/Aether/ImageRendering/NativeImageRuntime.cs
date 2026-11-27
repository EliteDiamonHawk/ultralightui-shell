using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DCommon;
using Vortice.DXGI;
using Vortice.Mathematics;
using D3DFeatureLevel = Vortice.Direct3D.FeatureLevel;
using D2DInterpolationMode = Vortice.Direct2D1.InterpolationMode;

namespace Aether;

internal sealed class NativeImageRuntime : IDisposable
{
    private const string WindowClassName = "Aether.NativeImageWidget";
    private static readonly NativeMethods.WndProc WindowProcedure = WindowProc;
    private static int _windowClassRegistered;

    private readonly GifDeadlineScheduler _scheduler = new();
    private readonly ID3D11Device _d3dDevice;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly Vortice.WIC.IWICImagingFactory _wicFactory;
    private readonly List<NativeImageWindow> _windows = new();
    private bool _disposed;

    public NativeImageRuntime()
    {
        EnsureWindowClass();
        _d3dDevice = CreateD3DDevice();
        _dxgiDevice = _d3dDevice.QueryInterface<IDXGIDevice>();
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(_dxgiDevice);
        _compositionDevice = DComp.DCompositionCreateDevice<IDCompositionDevice>(_dxgiDevice);
        _wicFactory = new Vortice.WIC.IWICImagingFactory();
    }

    public AppWindow CreateWindow(RendererWindowSpec spec, TimeSpan timeout)
    {
        var native = new NativeImageWindow(this, spec, timeout);
        _windows.Add(native);
        AppWindow appWindow = new(spec, timeout, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, native.Hwnd);
        appWindow.NativeImage = native;
        native.Attach(appWindow);
        if (native.IsAnimationActive)
            _scheduler.Schedule(native, native.NextDeadline);
        return appWindow;
    }

    public void ShowWhenReady(AppWindow appWindow, TimeSpan timeout)
    {
        if (appWindow.NativeImage != null && appWindow.DesiredVisible)
            NativeMethods.ShowWindow(appWindow.Hwnd, 8);
    }

    public bool Pump()
    {
        if (_disposed)
            return false;
        _scheduler.PresentDue(Stopwatch.GetTimestamp());
        return true;
    }

    public long? NextDeadline => _scheduler.NextDeadline;

    public bool TrySetPaused(AppWindow appWindow, bool paused)
    {
        NativeImageWindow? window = appWindow.NativeImage;
        if (window == null || appWindow.State is AppWindowState.Closed or AppWindowState.Failed)
            return false;
        window.SetPaused(paused);
        _scheduler.Remove(window);
        if (window.IsAnimationActive)
            _scheduler.Schedule(window, window.NextDeadline);
        return true;
    }

    public void DestroyWindow(AppWindow appWindow)
    {
        NativeImageWindow? native = appWindow.NativeImage;
        if (native == null)
            return;
        _scheduler.Remove(native);
        _windows.Remove(native);
        native.Dispose();
        appWindow.NativeImage = null;
        appWindow.Hwnd = IntPtr.Zero;
        appWindow.RendererPaused = false;
        appWindow.State = AppWindowState.Closed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (NativeImageWindow window in _windows.ToArray())
            window.Dispose();
        _windows.Clear();
        _wicFactory.Dispose();
        _compositionDevice.Dispose();
        _d2dDevice.Dispose();
        _d2dFactory.Dispose();
        _dxgiDevice.Dispose();
        _d3dDevice.Dispose();
    }

    internal IDCompositionDevice CompositionDevice => _compositionDevice;
    internal Vortice.WIC.IWICImagingFactory WicFactory => _wicFactory;

    private static void EnsureWindowClass()
    {
        if (Interlocked.Exchange(ref _windowClassRegistered, 1) != 0)
            return;
        var windowClass = new NativeMethods.WNDCLASSEX
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            WindowProcedure = WindowProcedure,
            Instance = NativeMethods.GetModuleHandleW(null),
            ClassName = WindowClassName
        };
        NativeMethods.RegisterClassExW(ref windowClass);
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam) =>
        NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);

    internal static IntPtr CreateNativeWindow(RendererWindowSpec spec)
    {
        uint extendedStyle = (uint)(NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);
        uint style = (uint)(NativeMethods.WS_POPUP | NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN);
        IntPtr hwnd = NativeMethods.CreateWindowExW(
            extendedStyle,
            WindowClassName,
            spec.EffectiveTitle,
            style,
            spec.Bounds.Left,
            spec.Bounds.Top,
            Math.Max(1, spec.Bounds.Width),
            Math.Max(1, spec.Bounds.Height),
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.GetModuleHandleW(null),
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"Native image window could not be created: {spec.LogicalId}");
        return hwnd;
    }

    internal static void ThrowIfFailed(bool succeeded, string operation)
    {
        if (!succeeded)
            throw new InvalidOperationException($"Native image renderer failed to {operation}.");
    }

    private static ID3D11Device CreateD3DDevice()
    {
        var levels = new[] { D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_1, D3DFeatureLevel.Level_10_0 };
        try
        {
            return D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels);
        }
        catch
        {
            return D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, levels);
        }
    }
}

internal sealed class NativeImageWindow : IGifDeadlineClient, IDisposable
{
    private readonly NativeImageRuntime _runtime;
    private readonly RendererWindowSpec _spec;
    private readonly IDCompositionTarget? _target;
    private readonly IDCompositionVisual? _visual;
    private readonly IDCompositionVisual3? _visual3;
    private readonly IDCompositionSurface? _surface;
    private readonly NativeImageAsset? _asset;
    private readonly GifCompositor? _compositor;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private AppWindow? _appWindow;
    private bool _paused;
    private bool _disposed;
    private bool _forceFullUpdate;
    private int _nextFrame;
    private int _completedLoops;
    private long _nextDeadline;

    public NativeImageWindow(NativeImageRuntime runtime, RendererWindowSpec spec, TimeSpan timeout)
    {
        _runtime = runtime;
        _spec = spec;
        Hwnd = NativeImageRuntime.CreateNativeWindow(spec);
        try
        {
            _asset = NativeImageAsset.Load(spec.Image?.SourcePath ?? throw new InvalidOperationException("Native image source is missing."),
                spec.Image?.AssetType ?? ImageAssetType.Auto, _runtime.WicFactory);
            NativeImageAsset asset = _asset;
            _canvasWidth = asset.Width;
            _canvasHeight = asset.Height;
            if (asset.Animation != null)
                _compositor = new GifCompositor(_canvasWidth, _canvasHeight, asset.Animation.Background.Span);

            bool topmost = spec.EffectiveSurface.ZOrder == WindowZOrder.Topmost;
            _runtime.CompositionDevice.CreateTargetForHwnd(Hwnd, topmost, out _target);
            _runtime.CompositionDevice.CreateVisual(out _visual);
            try { _visual3 = _visual.QueryInterface<IDCompositionVisual3>(); }
            catch { _visual3 = null; }
            _runtime.CompositionDevice.CreateSurface(
                (uint)Math.Max(1, spec.Bounds.Width),
                (uint)Math.Max(1, spec.Bounds.Height),
                Format.B8G8R8A8_UNorm,
                Vortice.DXGI.AlphaMode.Premultiplied,
                out _surface);
            _visual!.SetContent(_surface);
            _target!.SetRoot(_visual);
            _visual3?.SetOpacityMode(OpacityMode.Layer);
            if (Math.Abs(spec.Image?.RotationDegrees ?? 0) > 0.001)
            {
                float radians = (float)((spec.Image?.RotationDegrees ?? 0) * Math.PI / 180.0);
                Matrix3x2 matrix = Matrix3x2.CreateRotation(
                    radians,
                    new Vector2(spec.Bounds.Width / 2f, spec.Bounds.Height / 2f));
                _visual.SetTransform(matrix);
            }

            if (asset.Animation != null)
            {
                GifFrame firstFrame = asset.Animation.DecodeFrame(0);
                _compositor!.Compose(firstFrame);
                _nextFrame = 1;
                _nextDeadline = Stopwatch.GetTimestamp() + GifDeadlineScheduler.MillisecondsToTicks(
                    ScaleDelay(firstFrame.DelayMilliseconds));
                Render(_compositor.Canvas, _compositor.Width, _compositor.Height, new PixelRect(0, 0, _canvasWidth, _canvasHeight));
            }
            else
            {
                Render(asset.Pixels!, asset.Width, asset.Height, new PixelRect(0, 0, asset.Width, asset.Height));
            }
            _runtime.CompositionDevice.Commit();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IntPtr Hwnd { get; }
    public bool IsAnimationActive => !_paused && !_disposed && _asset?.Animation != null && _nextDeadline > 0;
    public long NextDeadline => _nextDeadline;

    public void Attach(AppWindow appWindow) => _appWindow = appWindow;

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (!paused && _asset?.Animation != null)
            _nextDeadline = Stopwatch.GetTimestamp() + GifDeadlineScheduler.MillisecondsToTicks(10);
    }

    public long PresentDueFrames(long nowTicks)
    {
        GifAnimation? animation = _asset?.Animation;
        if (_paused || animation == null || _disposed)
            return 0;

        int presented = 0;
        while (_nextFrame < animation.FrameCount && nowTicks >= _nextDeadline && presented++ < 8)
        {
            GifFrame frame = animation.DecodeFrame(_nextFrame++);
            GifCompositionResult result = _compositor!.Compose(frame);
            PixelRect dirty = _forceFullUpdate
                ? new PixelRect(0, 0, _compositor.Width, _compositor.Height)
                : result.DirtyRegion;
            Render(_compositor.Canvas, _compositor.Width, _compositor.Height, dirty);
            _forceFullUpdate = false;
            _nextDeadline += GifDeadlineScheduler.MillisecondsToTicks(ScaleDelay(frame.DelayMilliseconds));
        }

        if (_nextFrame >= animation.FrameCount && nowTicks >= _nextDeadline)
        {
            _completedLoops++;
            if (animation.RepeatCount >= 0 && _completedLoops >= animation.RepeatCount)
            {
                _nextDeadline = 0;
                return 0;
            }

            _compositor!.ResetLoop();
            _forceFullUpdate = true;
            _nextFrame = 0;
            _nextDeadline = nowTicks;
        }
        return _nextDeadline;
    }

    private long ScaleDelay(long milliseconds) =>
        Math.Max(1, (long)Math.Round(milliseconds / Math.Max(0.05, _spec.Image?.PlaybackSpeed ?? 1), MidpointRounding.AwayFromZero));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _nextDeadline = 0;
        if (Hwnd != IntPtr.Zero)
            NativeMethods.DestroyWindow(Hwnd);
        _surface?.Dispose();
        _target?.Dispose();
        _visual3?.Dispose();
        _visual?.Dispose();
        _asset?.Dispose();
    }

    private void Render(ReadOnlyMemory<byte> pixels, int sourceWidth, int sourceHeight, PixelRect dirty)
    {
        int width = Math.Max(1, _spec.Bounds.Width);
        int height = Math.Max(1, _spec.Bounds.Height);
        PixelRect update = dirty.Clamp(sourceWidth, sourceHeight);
        double threshold = _spec.Image?.DirtyRegionFullUpdateThreshold ?? .65;
        bool fullUpdate = update.IsEmpty || update.Area > sourceWidth * (long)sourceHeight * threshold;
        if (fullUpdate)
            update = new PixelRect(0, 0, sourceWidth, sourceHeight);

        (RawRectF fitDestination, RawRectF? fitSource) = GetFitRect(sourceWidth, sourceHeight, width, height);
        RawRect updateRect = fullUpdate
            ? new RawRect(0, 0, width, height)
            : MapDirtyToTarget(update, sourceWidth, sourceHeight, fitDestination, fitSource, width, height);
        if (updateRect.Right <= updateRect.Left || updateRect.Bottom <= updateRect.Top)
            return;

        IDCompositionSurface surface = _surface ?? throw new InvalidOperationException("Native image surface is not initialized.");
        surface.BeginDraw(updateRect, out ID2D1DeviceContext? drawContext, out Int2 _);
        if (drawContext == null)
            throw new InvalidOperationException("DirectComposition did not return a Direct2D device context.");
        try
        {
            drawContext.BeginDraw();
            drawContext.PushAxisAlignedClip(
                new RawRectF(updateRect.Left, updateRect.Top, updateRect.Right, updateRect.Bottom),
                AntialiasMode.Aliased);
            drawContext.Clear(new Color4(0, 0, 0, 0));
            if (!MemoryMarshal.TryGetArray(pixels, out ArraySegment<byte> segment) || segment.Array == null)
                throw new InvalidOperationException("Native image memory is not backed by an array.");
            var handle = GCHandle.Alloc(segment.Array, GCHandleType.Pinned);
            try
            {
                var bitmapProperties = new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96,
                    96,
                    BitmapOptions.None);
                using ID2D1Bitmap bitmap = drawContext.CreateBitmap(
                    new SizeI(sourceWidth, sourceHeight),
                    IntPtr.Add(handle.AddrOfPinnedObject(), segment.Offset),
                    (uint)(sourceWidth * 4),
                    bitmapProperties);
                drawContext.DrawBitmap(bitmap, fitDestination,
                    (float)Math.Clamp(_spec.Image?.Opacity ?? 1, 0, 1),
                    D2DInterpolationMode.HighQualityCubic, fitSource, null);
            }
            finally
            {
                handle.Free();
            }
            drawContext.PopAxisAlignedClip();
            drawContext.EndDraw();
        }
        finally
        {
            drawContext.Dispose();
            surface.EndDraw();
        }
        _runtime.CompositionDevice.Commit();
    }

    private static RawRect MapDirtyToTarget(PixelRect dirty, int sourceWidth, int sourceHeight,
        RawRectF destination, RawRectF? source, int targetWidth, int targetHeight)
    {
        RawRectF sourceRect = source ?? new RawRectF(0, 0, sourceWidth, sourceHeight);
        float left = Math.Max(dirty.X, sourceRect.Left);
        float top = Math.Max(dirty.Y, sourceRect.Top);
        float right = Math.Min(dirty.X + dirty.Width, sourceRect.Right);
        float bottom = Math.Min(dirty.Y + dirty.Height, sourceRect.Bottom);
        if (right <= left || bottom <= top)
            return new RawRect(0, 0, 0, 0);

        float scaleX = (destination.Right - destination.Left) / (sourceRect.Right - sourceRect.Left);
        float scaleY = (destination.Bottom - destination.Top) / (sourceRect.Bottom - sourceRect.Top);
        int targetLeft = Math.Clamp((int)Math.Floor(destination.Left + (left - sourceRect.Left) * scaleX), 0, targetWidth);
        int targetTop = Math.Clamp((int)Math.Floor(destination.Top + (top - sourceRect.Top) * scaleY), 0, targetHeight);
        int targetRight = Math.Clamp((int)Math.Ceiling(destination.Left + (right - sourceRect.Left) * scaleX), 0, targetWidth);
        int targetBottom = Math.Clamp((int)Math.Ceiling(destination.Top + (bottom - sourceRect.Top) * scaleY), 0, targetHeight);
        return new RawRect(targetLeft, targetTop, targetRight, targetBottom);
    }

    private (RawRectF Destination, RawRectF? Source) GetFitRect(int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
    {
        ImageFitMode fit = _spec.Image?.Fit ?? ImageFitMode.Contain;
        if (fit == ImageFitMode.Stretch)
            return (new RawRectF(0, 0, targetWidth, targetHeight), null);

        double sourceAspect = sourceWidth / (double)sourceHeight;
        double targetAspect = targetWidth / (double)targetHeight;
        if (fit == ImageFitMode.Contain)
        {
            float drawWidth = (float)(sourceAspect > targetAspect ? targetWidth : targetHeight * sourceAspect);
            float drawHeight = (float)(sourceAspect > targetAspect ? targetWidth / sourceAspect : targetHeight);
            return (new RawRectF((targetWidth - drawWidth) / 2, (targetHeight - drawHeight) / 2,
                (targetWidth - drawWidth) / 2 + drawWidth, (targetHeight - drawHeight) / 2 + drawHeight), null);
        }

        if (sourceAspect > targetAspect)
        {
            float cropWidth = (float)(sourceHeight * targetAspect);
            float left = (sourceWidth - cropWidth) / 2;
            return (new RawRectF(0, 0, targetWidth, targetHeight), new RawRectF(left, 0, left + cropWidth, sourceHeight));
        }
        float cropHeight = (float)(sourceWidth / targetAspect);
        float top = (sourceHeight - cropHeight) / 2;
        return (new RawRectF(0, 0, targetWidth, targetHeight), new RawRectF(0, top, sourceWidth, top + cropHeight));
    }
}
