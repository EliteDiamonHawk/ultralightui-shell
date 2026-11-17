using System.Runtime.InteropServices;
using System.Text.Json;

namespace Aether;

internal sealed class RendererJsBridge : IDisposable
{
    private readonly RendererWindowSpec _spec;
    private readonly UltralightNative.JsFunctionCallback _openPipeCallback;
    private IntPtr _api;
    private IntPtr _view;
    private bool _attached;

    public RendererJsBridge(RendererWindowSpec spec)
    {
        _spec = spec;
        _openPipeCallback = OpenPipe;
    }

    public bool IsAttached => _attached;

    public void Attach(IntPtr view)
    {
        if (_attached || view == IntPtr.Zero || _spec.Bridge?.IsEnabled(_spec.Url) != true)
            return;

        IntPtr api = UltralightNative.ulCreateJSAPI("renderer");
        if (api == IntPtr.Zero)
            throw new InvalidOperationException($"JavaScript bridge could not be created: {_spec.LogicalId}");

        try
        {
            if (!UltralightNative.ulJSAPISetConstantString(api, "windowId", _spec.LogicalId) ||
                !UltralightNative.ulJSAPISetConstantString(api, "pipeName", $"renderer://backend/{_spec.LogicalId}") ||
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
        _api = IntPtr.Zero;
        _view = IntPtr.Zero;
        _attached = false;
    }

    private static IntPtr OpenPipe(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception)
    {
        exception = IntPtr.Zero;
        return UltralightNative.ulCreateJSValueStringFromCString(context, "renderer://backend");
    }
}
