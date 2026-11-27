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
    private static readonly object WindowClassSync = new();
    private static int _windowClassRegistered;

    private readonly GifDeadlineScheduler _scheduler = new();
    private readonly ID3D11Device _d3dDevice;
    private readonly IDXGIDevice _dxgiDevice;
    private readonly ID2D1Factory1 _d2dFactory;
    private readonly ID2D1Device _d2dDevice;
    private readonly IDCompositionDevice _compositionDevice;
    private readonly Vortice.WIC.IWICImagingFactory _wicFactory;
    private readonly List<NativeImageWindow> _windows = new();
    private readonly int _ownerThreadId;
    private bool _disposed;

    public NativeImageRuntime() : this(forceWarp: false)
    {
    }

    internal NativeImageRuntime(bool forceWarp)
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        EnsureWindowClass();
        using var resources = new NativeResourceScope();
        _d3dDevice = resources.Own(CreateD3DDevice(forceWarp));
        _dxgiDevice = resources.Own(_d3dDevice.QueryInterface<IDXGIDevice>());
        _d2dFactory = resources.Own(D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.SingleThreaded));
        _d2dDevice = resources.Own(_d2dFactory.CreateDevice(_dxgiDevice));
        // Associate DirectComposition with the D2D device so BeginDraw can
        // return an ID2D1DeviceContext for the surface update.
        _compositionDevice = resources.Own(DComp.DCompositionCreateDevice2<IDCompositionDevice>(_d2dDevice));
        _wicFactory = resources.Own(new Vortice.WIC.IWICImagingFactory());
        resources.Commit();
    }

    public AppWindow CreateWindow(RendererWindowSpec spec, TimeSpan timeout)
    {
        EnsureOwnerThread();
        NativeImageWindow? native = null;
        AppWindow? appWindow = null;
        try
        {
            native = new NativeImageWindow(this, spec, timeout);
            _windows.Add(native);
            appWindow = new AppWindow(spec, timeout, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, native.Hwnd);
            appWindow.NativeImage = native;
            native.Attach(appWindow);
            if (native.IsAnimationActive)
                _scheduler.Schedule(native, native.NextDeadline);
            return appWindow;
        }
        catch
        {
            if (native != null)
            {
                _scheduler.Remove(native);
                _windows.Remove(native);
                native.Dispose();
            }

            if (appWindow != null)
            {
                appWindow.NativeImage = null;
                appWindow.Hwnd = IntPtr.Zero;
            }

            throw;
        }
    }

    public void ShowWhenReady(AppWindow appWindow, TimeSpan timeout)
    {
        EnsureOwnerThread();
        if (appWindow.NativeImage != null && appWindow.DesiredVisible)
            NativeMethods.ShowWindow(appWindow.Hwnd, 8);
    }

    public bool Pump()
    {
        EnsureOwnerThread();
        if (_disposed)
            return false;
        _scheduler.PresentDue(Stopwatch.GetTimestamp());
        return true;
    }

    public long? NextDeadline => _scheduler.NextDeadline;

    public bool TrySetPaused(AppWindow appWindow, bool paused)
    {
        EnsureOwnerThread();
        NativeImageWindow? window = appWindow.NativeImage;
        if (window == null || appWindow.State is AppWindowState.Closed or AppWindowState.Failed)
            return false;
        bool changed = window.SetPaused(paused);
        if (!changed)
            return true;

        _scheduler.Remove(window);
        if (window.IsAnimationActive)
            _scheduler.Schedule(window, window.NextDeadline);
        return true;
    }

    public void DestroyWindow(AppWindow appWindow)
    {
        EnsureOwnerThread();
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
        EnsureOwnerThread();
        if (_disposed)
            return;
        _disposed = true;
        foreach (NativeImageWindow window in _windows.ToArray())
        {
            _scheduler.Remove(window);
            window.Dispose();
        }
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

    internal void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException("Native image resources must be accessed on their owning runtime thread.");
    }

    private static void EnsureWindowClass()
    {
        if (Volatile.Read(ref _windowClassRegistered) != 0)
            return;

        lock (WindowClassSync)
        {
            if (_windowClassRegistered != 0)
                return;

            var windowClass = new NativeMethods.WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                WindowProcedure = WindowProcedure,
                Instance = NativeMethods.GetModuleHandleW(null),
                ClassName = WindowClassName
            };
            if (NativeMethods.RegisterClassExW(ref windowClass) == 0 &&
                Marshal.GetLastPInvokeError() != 1410) // ERROR_CLASS_ALREADY_EXISTS
            {
                throw new InvalidOperationException(
                    $"Native image window class could not be registered: {Marshal.GetLastPInvokeError()}.");
            }

            Volatile.Write(ref _windowClassRegistered, 1);
        }
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

    private static ID3D11Device CreateD3DDevice(bool forceWarp)
    {
        var levels = new[] { D3DFeatureLevel.Level_11_0, D3DFeatureLevel.Level_10_1, D3DFeatureLevel.Level_10_0 };
        if (forceWarp)
            return D3D11.D3D11CreateDevice(DriverType.Warp, DeviceCreationFlags.BgraSupport, levels);

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
    private readonly GifAnimationTiming _timing = new();
    private bool _disposed;
    private bool _forceFullUpdate;
    private int _nextFrame;
    private int _completedLoops;

    private IntPtr _hwnd;

    public NativeImageWindow(NativeImageRuntime runtime, RendererWindowSpec spec, TimeSpan timeout)
    {
        _runtime = runtime;
        _spec = spec;
        _hwnd = NativeImageRuntime.CreateNativeWindow(spec);
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
                _timing.Start(
                    Stopwatch.GetTimestamp(),
                    GifDeadlineScheduler.MillisecondsToTicks(ScaleDelay(firstFrame.DelayMilliseconds)));
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

    public IntPtr Hwnd => _hwnd;
    public bool IsAnimationActive => !_disposed && _asset?.Animation != null && _timing.IsActive;
    public long NextDeadline => _timing.NextDeadline;

    public void Attach(AppWindow appWindow) => _appWindow = appWindow;

    public bool SetPaused(bool paused) => _timing.SetPaused(paused, Stopwatch.GetTimestamp());

    public long PresentDueFrames(long nowTicks)
    {
        GifAnimation? animation = _asset?.Animation;
        if (!_timing.IsActive || animation == null || _disposed)
            return 0;

        int presented = 0;
        while (_nextFrame < animation.FrameCount && nowTicks >= _timing.NextDeadline && presented++ < 8)
        {
            GifFrame frame = animation.DecodeFrame(_nextFrame++);
            GifCompositionResult result = _compositor!.Compose(frame);
            PixelRect dirty = _forceFullUpdate
                ? new PixelRect(0, 0, _compositor.Width, _compositor.Height)
                : result.DirtyRegion;
            Render(_compositor.Canvas, _compositor.Width, _compositor.Height, dirty);
            _forceFullUpdate = false;
            _timing.Advance(GifDeadlineScheduler.MillisecondsToTicks(ScaleDelay(frame.DelayMilliseconds)));
        }

        if (_nextFrame >= animation.FrameCount && nowTicks >= _timing.NextDeadline)
        {
            _completedLoops++;
            if (animation.RepeatCount >= 0 && _completedLoops >= animation.RepeatCount)
            {
                _timing.Complete();
                return 0;
            }

            _compositor!.ResetLoop();
            _forceFullUpdate = true;
            _nextFrame = 0;
            _timing.ResetLoop(nowTicks);
        }
        return _timing.NextDeadline;
    }

    private long ScaleDelay(long milliseconds) =>
        Math.Max(1, (long)Math.Round(milliseconds / Math.Max(0.05, _spec.Image?.PlaybackSpeed ?? 1), MidpointRounding.AwayFromZero));

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timing.Complete();
        _surface?.Dispose();
        _visual3?.Dispose();
        _visual?.Dispose();
        _target?.Dispose();
        _asset?.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    private void Render(ReadOnlyMemory<byte> pixels, int sourceWidth, int sourceHeight, PixelRect dirty)
    {
        int width = Math.Max(1, _spec.Bounds.Width);
        int height = Math.Max(1, _spec.Bounds.Height);
        // HighQualityCubic can sample outside the exact changed source region.
        // Until the native path has a validated filter-footprint implementation,
        // redraw the complete composition surface to preserve pixel equivalence.
        _ = dirty.Clamp(sourceWidth, sourceHeight);

        (RawRectF fitDestination, RawRectF? fitSource) = GetFitRect(sourceWidth, sourceHeight, width, height);
        RawRect updateRect = new RawRect(0, 0, width, height);
        if (updateRect.Right <= updateRect.Left || updateRect.Bottom <= updateRect.Top)
            return;

        IDCompositionSurface surface = _surface ?? throw new InvalidOperationException("Native image surface is not initialized.");
        ID2D1DeviceContext? drawContext = null;
        bool surfaceDrawBegun = false;
        try
        {
            surface.BeginDraw(updateRect, out drawContext, out Int2 updateOffset);
            surfaceDrawBegun = true;
            if (drawContext == null)
                throw new InvalidOperationException("DirectComposition did not return a Direct2D device context.");

            Matrix3x2 updateTransform = CreateBeginDrawTransform(
                updateRect.Left,
                updateRect.Top,
                updateOffset.X,
                updateOffset.Y);
            drawContext.Transform = updateTransform;
            bool clipPushed = false;
            try
            {
                drawContext.PushAxisAlignedClip(
                    new RawRectF(updateRect.Left, updateRect.Top, updateRect.Right, updateRect.Bottom),
                    AntialiasMode.Aliased);
                clipPushed = true;
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
            }
            finally
            {
                if (clipPushed)
                    drawContext.PopAxisAlignedClip();
            }
        }
        finally
        {
            try
            {
                if (surfaceDrawBegun)
                    surface.EndDraw();
            }
            finally
            {
                drawContext?.Dispose();
            }
        }
        _runtime.CompositionDevice.Commit();
    }

    internal static Matrix3x2 CreateBeginDrawTransform(
        int requestedLeft,
        int requestedTop,
        int updateOffsetX,
        int updateOffsetY) =>
        Matrix3x2.CreateTranslation(updateOffsetX - requestedLeft, updateOffsetY - requestedTop);

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
