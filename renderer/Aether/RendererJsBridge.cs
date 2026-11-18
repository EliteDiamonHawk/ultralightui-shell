using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aether;

internal sealed class RendererJsBridge : IDisposable
{
    private readonly RendererWindowSpec _spec;
    private readonly BackendConnectionManager? _backends;
    private readonly ControlPipeChannel? _control;
    private readonly UltralightNative.JsFunctionCallback _openPipeCallback;
    private IntPtr _api;
    private IntPtr _view;
    private bool _attached;

    public RendererJsBridge(RendererWindowSpec spec, BackendConnectionManager? backends = null, ControlPipeChannel? control = null)
    {
        _spec = spec;
        _backends = backends;
        _control = control;
        if (_backends != null)
            _backends.StreamEventReceived += OnStreamEvent;
        _openPipeCallback = OpenPipe;
    }

    public bool IsAttached => _attached;

    public bool CanUseBackend(string backendId) =>
        _spec.Bridge?.IsEnabled(_spec.Url) == true &&
        _spec.Bridge.BackendIds.Contains(backendId, StringComparer.Ordinal);

    public bool CanUseControl(string path) =>
        _spec.Bridge?.IsEnabled(_spec.Url) == true &&
        _spec.Bridge.ControlRoutes.Contains(path, StringComparer.Ordinal);

    public Task<ControlResponse> RequestAsync(string backendId, string method, string path, object? body, CancellationToken cancellationToken = default)
    {
        if (!CanUseBackend(backendId) || _backends == null)
            throw new InvalidOperationException("Backend is not allowed for this renderer view.");
        return _backends.RequestAsync(backendId, method, path, body, cancellationToken);
    }

    public Task<BackendStreamDescriptor> OpenStreamAsync(string backendId, string dataId, string type, CancellationToken cancellationToken = default)
    {
        if (!CanUseBackend(backendId) || _backends == null)
            throw new InvalidOperationException("Backend is not allowed for this renderer view.");
        return _backends.OpenStreamAsync(backendId, dataId, type, cancellationToken);
    }

    public Task CloseStreamAsync(string backendId, string streamId, CancellationToken cancellationToken = default)
    {
        if (!CanUseBackend(backendId) || _backends == null)
            throw new InvalidOperationException("Backend is not allowed for this renderer view.");
        return _backends.CloseStreamAsync(backendId, streamId, cancellationToken);
    }

    public Task<ControlResponse> ControlAsync(string method, string path, object? body, CancellationToken cancellationToken = default)
    {
        if (!CanUseControl(path) || _control == null)
            throw new InvalidOperationException("Control route is not allowed for this renderer view.");
        return _control.SendAsync(method, path, body, TimeSpan.FromSeconds(30), cancellationToken);
    }

    public void Attach(IntPtr view)
    {
        if (_attached || view == IntPtr.Zero || _spec.Bridge?.IsEnabled(_spec.Url) != true)
            return;

        IntPtr api = UltralightNative.ulCreateJSAPI("renderer");
        if (api == IntPtr.Zero)
            throw new InvalidOperationException($"JavaScript bridge could not be created: {_spec.LogicalId}");

        try
        {
            string defaultBackend = _spec.Bridge.BackendIds.Count == 1 ? _spec.Bridge.BackendIds[0] : "unknown";
            if (!UltralightNative.ulJSAPISetConstantString(api, "windowId", _spec.LogicalId) ||
                !UltralightNative.ulJSAPISetConstantString(api, "pipeName", $"renderer://backend/{defaultBackend}") ||
                !UltralightNative.ulJSAPISetConstantJSON(api, "capabilities", JsonSerializer.Serialize(new
                {
                    request = false,
                    streams = false,
                    bindings = false,
                    backendIds = _spec.Bridge.BackendIds,
                    controlRoutes = _spec.Bridge.ControlRoutes
                })) ||
                !UltralightNative.ulJSAPIBindFunction(api, "openPipe", _openPipeCallback, IntPtr.Zero, IntPtr.Zero))
            {
                throw new InvalidOperationException($"JavaScript bridge bindings could not be registered: {_spec.LogicalId}");
            }

            IntPtr originRules = IntPtr.Zero;
            IntPtr originString = IntPtr.Zero;
            try
            {
                if (!_spec.Url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    Uri uri = new(_spec.Url);
                    string rule = $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? "" : $":{uri.Port}")}";
                    originString = Marshal.StringToCoTaskMemUTF8(rule);
                    originRules = Marshal.AllocHGlobal(IntPtr.Size);
                    Marshal.WriteIntPtr(originRules, originString);
                }

                if (!UltralightNative.ulViewAttachJSAPI(
                        view,
                        api,
                        UltralightNative.JsApiAttachFlagsNone,
                        originRules,
                        originRules == IntPtr.Zero ? UIntPtr.Zero : new UIntPtr(1)))
                {
                    throw new InvalidOperationException($"JavaScript bridge could not attach: {_spec.LogicalId}");
                }
            }
            finally
            {
                if (originRules != IntPtr.Zero) Marshal.FreeHGlobal(originRules);
                if (originString != IntPtr.Zero) Marshal.FreeCoTaskMem(originString);
            }

            _api = api;
            _view = view;
            _attached = true;
        }
        catch
        {
            UltralightNative.ulDestroyJSAPI(api);
            throw;
        }
    }

    public void EmitStreamEvent(string streamId, object data, string type = "json")
    {
        if (!_attached || _api == IntPtr.Zero)
            return;
        string args = JsonSerializer.Serialize(new[] { new { streamId, data, type } });
        UltralightNative.ulJSAPIEmitEventJSON(_api, "stream", args);
    }

    public void Dispose()
    {
        if (_api != IntPtr.Zero && _view != IntPtr.Zero && _attached)
        {
            try { UltralightNative.ulViewDetachJSAPI(_view, _api); } catch { }
        }
        if (_api != IntPtr.Zero)
            UltralightNative.ulDestroyJSAPI(_api);
        if (_backends != null)
            _backends.StreamEventReceived -= OnStreamEvent;
        _api = IntPtr.Zero;
        _view = IntPtr.Zero;
        _attached = false;
    }

    private IntPtr OpenPipe(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception)
    {
        exception = IntPtr.Zero;
        string backendId = _spec.Bridge?.BackendIds.Count == 1
            ? _spec.Bridge.BackendIds[0]
            : "unknown";
        return UltralightNative.ulCreateJSValueStringFromCString(context, $"renderer://backend/{backendId}");
    }

    private void OnStreamEvent(BackendEvent value)
    {
        if (value.Name == "stream.data" && value.Data is JsonElement data)
            EmitStreamEvent(value.StreamId, data, value.Type);
        else if (value.Name == "stream.overflow")
            EmitStreamEvent(value.StreamId, new { code = "stream_overflow" }, "json");
    }
}
