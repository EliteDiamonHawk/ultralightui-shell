using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aether;

internal sealed record LogicalBackendPipe(string PipeId, string PipeName, string BackendId, string State);

internal sealed class RendererJsBridge : IDisposable
{
    private static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private static readonly UltralightNative.JsCompleteCallback CompletePromiseCallback = CompletePromise;
    private static readonly UltralightNative.UserDataDestroyCallback DestroyPromiseStateCallback = DestroyPromiseState;
    private static readonly UltralightNative.DomDataSyncWalkCallback DomDataWalkCallback = WalkBinding;
    private static readonly UltralightNative.DomDataAcquireCallback DomDataAcquireCallback = AcquireBinding;
    private static readonly UltralightNative.UserDataDestroyCallback DestroyBindingHolderCallback = DestroyBindingHolder;
    private static readonly UltralightNative.UserDataDestroyCallback DestroySubscriptionStateCallback = DestroySubscriptionState;
    private const int JsErrorTypeError = 1;
    private const uint DomDataBool = 0;
    private const uint DomDataDouble = 2;
    private const uint DomDataString = 3;
    private const uint DomDataNoKey = 0xFF;
    private readonly RendererWindowSpec _spec;
    private readonly BackendConnectionManager? _backends;
    private readonly ControlPipeChannel? _control;
    private readonly Func<Action, bool> _ownerPoster;
    private readonly Func<RendererCommand, string>? _runtimeCommandExecutor;
    private readonly UltralightNative.JsFunctionCallback _openPipeCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _requestCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _openStreamCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _closeStreamCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _controlCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _runtimeCommandCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _createBindingCallback;
    private readonly UltralightNative.JsAsyncFunctionCallback _destroyBindingCallback;
    private readonly UltralightNative.JsFunctionCallback _onStreamCallback;
    private readonly UltralightNative.JsFunctionCallback _onEventCallback;
    private readonly UltralightNative.JsFunctionCallback _onCueCallback;
    private readonly UltralightNative.JsFunctionCallback _unsubscribeCallback;
    private readonly UltralightNative.JsFunctionCallback _getVariableCallback;
    private readonly List<NativeEventListener> _streamListeners = new();
    private readonly List<NativeEventListener> _eventListeners = new();
    private int _listenerSequence;
    private readonly object _streamLeaseSync = new();
    private readonly Dictionary<(string BackendId, string StreamId), int> _streamLeases = new();
    private readonly object _bindingSync = new();
    private readonly Dictionary<string, NativeBinding> _bindings = new(StringComparer.Ordinal);
    private IntPtr _api;
    private IntPtr _view;
    private IntPtr _dataContext;
    private bool _dataContextAttached;
    private bool _attached;
    private int _disposed;

    public RendererJsBridge(
        RendererWindowSpec spec,
        Func<Action, bool> ownerPoster,
        BackendConnectionManager? backends = null,
        ControlPipeChannel? control = null,
        Func<RendererCommand, string>? runtimeCommandExecutor = null)
    {
        _spec = spec;
        _backends = backends;
        _control = control;
        _ownerPoster = ownerPoster;
        _runtimeCommandExecutor = runtimeCommandExecutor;
        if (_backends != null)
            _backends.StreamEventReceived += OnStreamEvent;
        if (_control != null)
            _control.EventReceived += OnControlEvent;
        _openPipeCallback = OpenPipe;
        _requestCallback = Request;
        _openStreamCallback = OpenStream;
        _closeStreamCallback = CloseStream;
        _controlCallback = Control;
        _runtimeCommandCallback = RuntimeCommand;
        _createBindingCallback = CreateBinding;
        _destroyBindingCallback = DestroyBinding;
        _onStreamCallback = OnStream;
        _onEventCallback = OnEvent;
        _onCueCallback = OnCue;
        _unsubscribeCallback = Unsubscribe;
        _getVariableCallback = GetVariable;
    }

    public bool IsAttached => _attached;

    public bool CanUseBackend(string backendId) =>
        _spec.Bridge?.IsEnabled(_spec.Url) == true &&
        _spec.Bridge.BackendIds.Contains(backendId, StringComparer.Ordinal);

    public bool CanUseControl(string path) =>
        _spec.Bridge?.IsEnabled(_spec.Url) == true &&
        _spec.Bridge.ControlRoutes.Contains(path, StringComparer.Ordinal);

    public bool CanUseRuntimeCommand(string command) =>
        _spec.Bridge?.IsEnabled(_spec.Url) == true &&
        _spec.Bridge.RuntimeCommands.Contains(command, StringComparer.Ordinal);

    internal LogicalBackendPipe ResolveBackendPipe(string backendId)
    {
        if (!CanUseBackend(backendId))
            throw new InvalidOperationException("Backend is not allowed for this renderer view.");

        return new LogicalBackendPipe(
            $"{_spec.LogicalId}:{backendId}",
            $"renderer://backend/{backendId}",
            backendId,
            _backends == null || _control?.State != "connected" ? "unavailable" : "available");
    }

    public Task<BackendResponse> RequestAsync(string backendId, string method, string path, object? body, CancellationToken cancellationToken = default)
    {
        if (!CanUseBackend(backendId) || _backends == null || _control == null)
            throw new InvalidOperationException("Backend is not allowed for this renderer view.");
        return _backends.RequestAsync(backendId, method, path, body, cancellationToken);
    }

    public Task<BackendStreamDescriptor> OpenStreamAsync(string backendId, string dataId, string type, CancellationToken cancellationToken = default)
    {
        if (!CanUseBackend(backendId) || _backends == null || _control == null)
            throw new InvalidOperationException("Backend is not allowed for this renderer view.");
        return _backends.OpenStreamAsync(backendId, dataId, type, cancellationToken);
    }

    public Task CloseStreamAsync(string backendId, string streamId, CancellationToken cancellationToken = default)
    {
        if (!CanUseBackend(backendId) || _backends == null || _control == null)
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
        IntPtr dataContext = UltralightNative.ulCreateDOMDataContext();
        if (dataContext == IntPtr.Zero)
        {
            UltralightNative.ulDestroyJSAPI(api);
            throw new InvalidOperationException($"Ultralight data-binding context could not be created: {_spec.LogicalId}");
        }

        try
        {
            string defaultBackend = _spec.Bridge.BackendIds.Count == 1 ? _spec.Bridge.BackendIds[0] : "unknown";
            string capabilities = JsonSerializer.Serialize(new
            {
                request = _backends != null && _control != null,
                streams = _backends != null && _control != null,
                bindings = _backends != null && _control != null,
                variables = true,
                cues = _control != null && (_spec.Cues?.Count ?? 0) > 0,
                backendIds = _spec.Bridge.BackendIds,
                controlRoutes = _spec.Bridge.ControlRoutes,
                runtimeCommands = _spec.Bridge.RuntimeCommands
            }, BridgeJsonOptions);
            if (!UltralightNative.ulJSAPISetConstantString(api, "windowId", _spec.LogicalId) ||
                !UltralightNative.ulJSAPISetConstantString(api, "pipeName", $"renderer://backend/{defaultBackend}") ||
                !UltralightNative.ulJSAPISetConstantJSON(api, "capabilities", capabilities) ||
                !UltralightNative.ulJSAPIBindFunction(api, "aether.variables.get", _getVariableCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindFunction(api, "openPipe", _openPipeCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindAsyncFunction(api, "request", _requestCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindAsyncFunction(api, "openStream", _openStreamCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindAsyncFunction(api, "closeStream", _closeStreamCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindAsyncFunction(api, "control", _controlCallback, IntPtr.Zero, IntPtr.Zero) ||
                (_runtimeCommandExecutor != null && !UltralightNative.ulJSAPIBindAsyncFunction(api, "aether.command", _runtimeCommandCallback, IntPtr.Zero, IntPtr.Zero)) ||
                !UltralightNative.ulJSAPIBindAsyncFunction(api, "createBinding", _createBindingCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindAsyncFunction(api, "destroyBinding", _destroyBindingCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindFunction(api, "onStream", _onStreamCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindFunction(api, "aether.onEvent", _onEventCallback, IntPtr.Zero, IntPtr.Zero) ||
                !UltralightNative.ulJSAPIBindFunction(api, "aether.onCue", _onCueCallback, IntPtr.Zero, IntPtr.Zero))
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
                if (!UltralightNative.ulViewAttachDOMDataContext(
                        view,
                        dataContext,
                        0,
                        originRules,
                        originRules == IntPtr.Zero ? UIntPtr.Zero : new UIntPtr(1)))
                {
                    throw new InvalidOperationException($"Ultralight data-binding context could not attach: {_spec.LogicalId}");
                }
            }
            finally
            {
                if (originRules != IntPtr.Zero) Marshal.FreeHGlobal(originRules);
                if (originString != IntPtr.Zero) Marshal.FreeCoTaskMem(originString);
            }

            _api = api;
            _view = view;
            _dataContext = dataContext;
            _dataContextAttached = true;
            _attached = true;
            dataContext = IntPtr.Zero;
        }
        catch
        {
            if (dataContext != IntPtr.Zero)
                UltralightNative.ulDestroyDOMDataContext(dataContext);
            UltralightNative.ulDestroyJSAPI(api);
            throw;
        }
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
        if (!TryGetObject(args, argumentCount, out JsonElement input, out string error) ||
            !TryGetBackendId(input, out string backendId, out error))
        {
            exception = CreateTypeError(context, error.Length == 0 ? "Backend is not allowed for this renderer view." : error);
            return IntPtr.Zero;
        }

        try
        {
            LogicalBackendPipe pipe = ResolveBackendPipe(backendId);
            return CreateJsonValue(context, JsonSerializer.Serialize(pipe, BridgeJsonOptions), out exception);
        }
        catch (InvalidOperationException ex)
        {
            exception = CreateTypeError(context, ex.Message);
            return IntPtr.Zero;
        }
    }

    private void Request(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, async input =>
        {
            if (!TryGetBackendId(input, out string backendId, out string error) || !TryGetMethod(input, out string method, out error) || !TryGetPath(input, out string path, out error))
                return PromiseResult.Failure(error);
            if (!CanUseBackend(backendId) || _backends == null || _control == null)
                return PromiseResult.Failure("Backend is not allowed for this renderer view.");
            object? body = input.TryGetProperty("body", out JsonElement bodyValue) && bodyValue.ValueKind != JsonValueKind.Null ? bodyValue.Clone() : null;
            try
            {
                BackendResponse response = await _backends.RequestAsync(backendId, method, path, body).ConfigureAwait(false);
                return PromiseResult.Success(new { status = response.Status, body = response.Body, error = response.Error });
            }
            catch (Exception ex) { return PromiseResult.Failure(ex.Message); }
        });

    private void OpenStream(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, async input =>
        {
            if (!TryGetBackendId(input, out string backendId, out string error) || !TryGetString(input, "dataId", out string dataId, out error))
                return PromiseResult.Failure(error);
            string type = input.TryGetProperty("type", out JsonElement typeValue) && typeValue.ValueKind == JsonValueKind.String ? typeValue.GetString()! : "json";
            if (!CanUseBackend(backendId) || _backends == null || _control == null)
                return PromiseResult.Failure("Backend is not allowed for this renderer view.");
            try
            {
                BackendStreamDescriptor descriptor = await _backends.OpenStreamAsync(backendId, dataId, type).ConfigureAwait(false);
                bool releaseAfterDispose;
                lock (_streamLeaseSync)
                {
                    releaseAfterDispose = Volatile.Read(ref _disposed) != 0;
                    if (!releaseAfterDispose)
                    {
                        (string BackendId, string StreamId) key = (backendId, descriptor.StreamId);
                        _streamLeases[key] = _streamLeases.TryGetValue(key, out int count) ? count + 1 : 1;
                    }
                }
                if (releaseAfterDispose)
                {
                    try { await _backends.CloseStreamAsync(backendId, descriptor.StreamId).ConfigureAwait(false); } catch { }
                    return PromiseResult.Failure("Renderer view is disposed.");
                }
                return PromiseResult.Success(new { streamId = descriptor.StreamId, type = descriptor.Type, schema = descriptor.Schema });
            }
            catch (Exception ex) { return PromiseResult.Failure(ex.Message); }
        });

    private void CloseStream(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, async input =>
        {
            if (!TryGetBackendId(input, out string backendId, out string error) || !TryGetString(input, "streamId", out string streamId, out error))
                return PromiseResult.Failure(error);
            if (!CanUseBackend(backendId) || _backends == null || _control == null)
                return PromiseResult.Failure("Backend is not allowed for this renderer view.");
            (string BackendId, string StreamId) key = (backendId, streamId);
            lock (_streamLeaseSync)
            {
                if (!_streamLeases.TryGetValue(key, out int count) || count == 0)
                    return PromiseResult.Failure("Stream is not owned by this renderer view.");
                if (count == 1) _streamLeases.Remove(key);
                else _streamLeases[key] = count - 1;
            }
            try
            {
                await _backends.CloseStreamAsync(backendId, streamId).ConfigureAwait(false);
                return PromiseResult.Success(new { ok = true });
            }
            catch (Exception ex)
            {
                lock (_streamLeaseSync)
                    _streamLeases[key] = _streamLeases.TryGetValue(key, out int count) ? count + 1 : 1;
                return PromiseResult.Failure(ex.Message);
            }
        });

    private void Control(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, async input =>
        {
            if (!TryGetMethod(input, out string method, out string error) || !TryGetPath(input, out string path, out error))
                return PromiseResult.Failure(error);
            if (!CanUseControl(path) || _control == null)
                return PromiseResult.Failure("Control route is not allowed for this renderer view.");
            object? body = input.TryGetProperty("body", out JsonElement bodyValue) && bodyValue.ValueKind != JsonValueKind.Null ? bodyValue.Clone() : null;
            try
            {
                ControlResponse response = await _control.SendAsync(method, path, body, TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                return PromiseResult.Success(new { status = response.Status, body = response.Body, error = response.Error });
            }
            catch (Exception ex) { return PromiseResult.Failure(ex.Message); }
        });

    private void RuntimeCommand(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, input =>
        {
            if (!TryParseRuntimeCommand(input, out string commandName, out RendererCommand command, out string error))
                return Task.FromResult(PromiseResult.Failure(error));
            if (!CanUseRuntimeCommand(commandName))
                return Task.FromResult(PromiseResult.Failure("Runtime command is not allowed for this renderer view."));
            if (_runtimeCommandExecutor == null)
                return Task.FromResult(PromiseResult.Failure("Runtime commands are unavailable for this renderer view."));

            try
            {
                string response = _runtimeCommandExecutor(command);
                if (response.StartsWith("ERR ", StringComparison.Ordinal))
                    return Task.FromResult(PromiseResult.Failure(response[4..]));
                string message = response.StartsWith("OK ", StringComparison.Ordinal)
                    ? response[3..]
                    : response;
                return Task.FromResult(PromiseResult.Success(new { command = commandName, message }));
            }
            catch (Exception ex)
            {
                return Task.FromResult(PromiseResult.Failure(ex.Message));
            }
        });

    private void CreateBinding(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, async input =>
        {
            if (!TryGetString(input, "name", out string name, out string error) ||
                !TryGetBackendId(input, out string backendId, out error) ||
                !TryGetString(input, "dataId", out string dataId, out error))
                return PromiseResult.Failure(error);
            if (!IsDomName(name))
                return PromiseResult.Failure("name must contain only letters, digits, and underscores, and must not start with a digit.");
            if (!CanUseBackend(backendId) || _backends == null || _control == null)
                return PromiseResult.Failure("Backend is not allowed for this renderer view.");
            if (_dataContext == IntPtr.Zero)
                return PromiseResult.Failure("Data bindings are unavailable because the Ultralight data context is not attached.");
            if (!input.TryGetProperty("schema", out JsonElement schemaValue) || schemaValue.ValueKind != JsonValueKind.Object)
                return PromiseResult.Failure("schema must be the backend stream schema descriptor.");

            JsonSchemaDescriptor requestedSchema;
            try { requestedSchema = JsonSchemaDescriptor.Parse(schemaValue); }
            catch (Exception ex) { return PromiseResult.Failure(ex.Message); }

            BackendStreamDescriptor descriptor;
            try { descriptor = await _backends.OpenStreamAsync(backendId, dataId, "json").ConfigureAwait(false); }
            catch (Exception ex) { return PromiseResult.Failure(ex.Message); }
            if (!SchemasEqual(requestedSchema, descriptor.Schema))
            {
                try { await _backends.CloseStreamAsync(backendId, descriptor.StreamId).ConfigureAwait(false); } catch { }
                return PromiseResult.Failure("schema does not match the backend stream descriptor.");
            }

            try
            {
                NativeBinding binding = await InvokeOnOwnerAsync(() => CreateNativeBinding(name, backendId, descriptor)).ConfigureAwait(false);
                return PromiseResult.Success(new { bindingId = binding.BindingId, streamId = binding.StreamId, name = binding.Name });
            }
            catch (Exception ex)
            {
                try { await _backends.CloseStreamAsync(backendId, descriptor.StreamId).ConfigureAwait(false); } catch { }
                return PromiseResult.Failure(ex.Message);
            }
        });

    private void DestroyBinding(IntPtr userData, IntPtr context, IntPtr thisValue, IntPtr args, UIntPtr argumentCount, IntPtr resolver) =>
        BeginAsync(context, resolver, args, argumentCount, async input =>
        {
            if (!TryGetString(input, "bindingId", out string bindingId, out string error))
                return PromiseResult.Failure(error);

            NativeBinding? binding;
            lock (_bindingSync)
            {
                if (!_bindings.TryGetValue(bindingId, out binding))
                    return PromiseResult.Failure("Binding was not found or is already being destroyed.");
                if (Interlocked.Exchange(ref binding.Destroying, 1) != 0)
                    return PromiseResult.Failure("Binding was not found or is already being destroyed.");
            }

            Exception? nativeError = null;
            try
            {
                await InvokeOnOwnerAsync(() => RemoveNativeBinding(binding)).ConfigureAwait(false);
            }
            catch (Exception ex) { nativeError = ex; }

            // Native teardown must complete even when control/backend transport has already
            // failed. A stale backend lease is harmless here; manager disposal or disconnect
            // cleanup will release it.
            if (_backends != null)
            {
                try { await _backends.CloseStreamAsync(binding.BackendId, binding.StreamId).ConfigureAwait(false); }
                catch { }
            }

            return nativeError == null
                ? PromiseResult.Success(new { ok = true })
                : PromiseResult.Failure(nativeError.Message);
        });

    private IntPtr OnStream(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception)
    {
        exception = IntPtr.Zero;
        if (argumentCount.ToUInt64() < 1 || args == IntPtr.Zero)
        {
            exception = CreateTypeError(context, "onStream requires a callback function.");
            return IntPtr.Zero;
        }
        return RegisterListener(context, args, argumentCount, EventSource.Stream, false, out exception);
    }

    private IntPtr OnEvent(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception) =>
        RegisterListener(context, args, argumentCount, null, true, out exception);

    private IntPtr OnCue(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception) =>
        RegisterListener(context, args, argumentCount, EventSource.Cue, true, out exception);

    private IntPtr RegisterListener(
        IntPtr context,
        IntPtr args,
        UIntPtr argumentCount,
        EventSource? filter,
        bool returnUnsubscribe,
        out IntPtr exception)
    {
        exception = IntPtr.Zero;
        if (argumentCount.ToUInt64() < 1 || args == IntPtr.Zero)
        {
            exception = CreateTypeError(context, "An event callback function is required.");
            return IntPtr.Zero;
        }

        IntPtr callback = Marshal.ReadIntPtr(args);
        if (!UltralightNative.ulJSValueIsFunction(callback))
        {
            exception = CreateTypeError(context, "An event callback function is required.");
            return IntPtr.Zero;
        }

        IntPtr retained = UltralightNative.ulCreateJSValueRef(callback);
        if (retained == IntPtr.Zero)
        {
            exception = CreateTypeError(context, "The event callback could not be retained.");
            return IntPtr.Zero;
        }

        string id = $"event-listener-{Interlocked.Increment(ref _listenerSequence)}";
        var listener = new NativeEventListener(id, retained, filter);
        if (filter == EventSource.Stream)
            _streamListeners.Add(listener);
        else
            _eventListeners.Add(listener);

        if (!returnUnsubscribe)
            return UltralightNative.ulCreateJSValueStringFromCString(context, id);

        var state = new SubscriptionState(this, id);
        GCHandle stateHandle = GCHandle.Alloc(state);
        IntPtr function = UltralightNative.ulCreateJSFunction(
            context,
            "unsubscribe",
            _unsubscribeCallback,
            GCHandle.ToIntPtr(stateHandle),
            DestroySubscriptionStateCallback);
        if (function == IntPtr.Zero)
            RemoveEventListener(id);
        return function;
    }

    private IntPtr GetVariable(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception)
    {
        exception = IntPtr.Zero;
        if (argumentCount.ToUInt64() < 1 || args == IntPtr.Zero)
        {
            exception = CreateTypeError(context, "A variable key string is required.");
            return IntPtr.Zero;
        }

        string? key = null;
        string? json = ReadJson(Marshal.ReadIntPtr(args));
        if (json != null)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement value = document.RootElement;
                if (value.ValueKind == JsonValueKind.String)
                    key = value.GetString();
                else if (value.ValueKind == JsonValueKind.Object &&
                         value.TryGetProperty("key", out JsonElement keyValue) &&
                         keyValue.ValueKind == JsonValueKind.String)
                    key = keyValue.GetString();
            }
            catch (JsonException) { }
        }
        if (string.IsNullOrWhiteSpace(key))
        {
            exception = CreateTypeError(context, "A variable key string is required.");
            return IntPtr.Zero;
        }

        if (_spec.Variables == null || !_spec.Variables.TryGetValue(key.Trim(), out JsonElement variableValue))
            return UltralightNative.ulCreateJSValueUndefined(context);

        return CreateJsonValue(context, variableValue.GetRawText(), out exception);
    }

    private static IntPtr Unsubscribe(
        IntPtr userData,
        IntPtr context,
        IntPtr thisValue,
        IntPtr args,
        UIntPtr argumentCount,
        out IntPtr exception)
    {
        exception = IntPtr.Zero;
        if (userData != IntPtr.Zero && GCHandle.FromIntPtr(userData).Target is SubscriptionState state)
            state.Bridge.RemoveEventListener(state.ListenerId);
        return IntPtr.Zero;
    }

    private static void DestroySubscriptionState(IntPtr userData)
    {
        if (userData != IntPtr.Zero)
        {
            try { GCHandle.FromIntPtr(userData).Free(); }
            catch (InvalidOperationException) { }
        }
    }

    private void RemoveEventListener(string id)
    {
        RemoveListener(_eventListeners, id);
        RemoveListener(_streamListeners, id);
    }

    private static void RemoveListener(List<NativeEventListener> listeners, string id)
    {
        int index = listeners.FindIndex(listener => listener.Id == id);
        if (index < 0)
            return;
        NativeEventListener listener = listeners[index];
        listeners.RemoveAt(index);
        UltralightNative.ulDestroyJSValue(listener.Callback);
    }

    private void BeginAsync(
        IntPtr context,
        IntPtr resolver,
        IntPtr args,
        UIntPtr argumentCount,
        Func<JsonElement, Task<PromiseResult>> operation)
    {
        if (resolver == IntPtr.Zero)
            return;

        if (!TryGetObject(args, argumentCount, out JsonElement input, out string error))
        {
            QueuePromiseResult(resolver, PromiseResult.Failure(error));
            return;
        }

        _ = FinishAsync(resolver, operation, input);
    }

    private static async Task FinishAsync(IntPtr resolver, Func<JsonElement, Task<PromiseResult>> operation, JsonElement input)
    {
        PromiseResult result;
        try { result = await operation(input).ConfigureAwait(false); }
        catch (Exception ex) { result = PromiseResult.Failure(ex.Message); }
        QueuePromiseResult(resolver, result);
    }

    private static void QueuePromiseResult(IntPtr resolver, PromiseResult result)
    {
        if (UltralightNative.ulJSPromiseResolverIsDetached(resolver))
        {
            UltralightNative.ulDestroyJSPromiseResolver(resolver);
            return;
        }

        GCHandle handle = GCHandle.Alloc(result);
        UltralightNative.ulJSPromiseResolverComplete(
            resolver,
            CompletePromiseCallback,
            GCHandle.ToIntPtr(handle),
            DestroyPromiseStateCallback);
        UltralightNative.ulDestroyJSPromiseResolver(resolver);
    }

    private static IntPtr CompletePromise(IntPtr userData, IntPtr context, out IntPtr exception)
    {
        exception = IntPtr.Zero;
        PromiseResult result = (PromiseResult)GCHandle.FromIntPtr(userData).Target!;
        if (result.Error != null)
        {
            exception = CreateTypeError(context, result.Error);
            return IntPtr.Zero;
        }
        return CreateJsonValue(context, result.Json!, out exception);
    }

    private static void DestroyPromiseState(IntPtr userData)
    {
        if (userData != IntPtr.Zero)
            GCHandle.FromIntPtr(userData).Free();
    }

    private void EmitStreamEvent(string? backendId, string streamId, object data, string type = "json")
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _ownerPoster(() => EmitStreamEventOnOwner(backendId, streamId, data, type));
    }

    private void EmitStreamEventOnOwner(string? backendId, string streamId, object data, string type)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_attached || _api == IntPtr.Zero)
            return;
        if (data is JsonElement jsonData)
            SyncBindingsOnOwner(backendId, streamId, jsonData);
        string args = JsonSerializer.Serialize(new[] { new { streamId, data, type } }, BridgeJsonOptions);
        UltralightNative.ulJSAPIEmitEventJSON(_api, "stream", args);
        EmitUnifiedEventOnOwner(
            EventSource.Stream,
            new { source = "stream", backendId, streamId, data, type },
            new { streamId, data, type });
    }

    private void OnControlEvent(string name, JsonElement? body)
    {
        if (Volatile.Read(ref _disposed) != 0 || !string.Equals(name, "cue", StringComparison.Ordinal) ||
            body is not { } cueBody || cueBody.ValueKind != JsonValueKind.Object ||
            !cueBody.TryGetProperty("name", out JsonElement cueNameValue) ||
            cueNameValue.ValueKind != JsonValueKind.String)
            return;

        string cueName = cueNameValue.GetString()?.Trim() ?? "";
        if (cueName.Length == 0 || _spec.Cues?.Contains(cueName, StringComparer.Ordinal) != true)
            return;

        JsonElement? data = cueBody.TryGetProperty("data", out JsonElement dataValue)
            ? dataValue.Clone()
            : null;
        EmitCueEvent(cueName, data);
    }

    private void EmitCueEvent(string name, JsonElement? data)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _ownerPoster(() =>
        {
            if (Volatile.Read(ref _disposed) != 0 || !_attached || _api == IntPtr.Zero)
                return;
            EmitUnifiedEventOnOwner(EventSource.Cue, new { source = "cue", name, data }, legacyCuePayload: new { name, data });
        });
    }

    private void EmitUnifiedEventOnOwner(
        EventSource source,
        object payload,
        object? legacyStreamPayload = null,
        object? legacyCuePayload = null)
    {
        string args = JsonSerializer.Serialize(new[] { payload }, BridgeJsonOptions);
        UltralightNative.ulJSAPIEmitEventJSON(_api, "event", args);
        foreach (NativeEventListener listener in _eventListeners
                     .Where(listener => listener.Filter == null || listener.Filter == source)
                     .ToArray())
        {
            object listenerPayload = listener.Filter switch
            {
                EventSource.Stream when legacyStreamPayload != null => legacyStreamPayload,
                EventSource.Cue when legacyCuePayload != null => legacyCuePayload,
                _ => payload
            };
            InvokeListener(listener, JsonSerializer.Serialize(listenerPayload, BridgeJsonOptions));
        }

        if (source == EventSource.Stream)
        {
            string streamPayload = JsonSerializer.Serialize(legacyStreamPayload ?? payload, BridgeJsonOptions);
            foreach (NativeEventListener listener in _streamListeners.ToArray())
                InvokeListener(listener, streamPayload);
        }
    }

    private void InvokeListener(NativeEventListener listener, string payload)
    {
        if (!UltralightNative.ulJSValueIsAlive(listener.Callback))
        {
            RemoveEventListener(listener.Id);
            return;
        }
        IntPtr context = UltralightNative.ulJSValueGetContext(listener.Callback);
        if (context == IntPtr.Zero)
            return;
        IntPtr value = CreateJsonValue(context, payload, out _);
        if (value == IntPtr.Zero)
        {
            UltralightNative.ulDestroyJSContext(context);
            return;
        }
        IntPtr argumentArray = Marshal.AllocHGlobal(IntPtr.Size);
        try
        {
            Marshal.WriteIntPtr(argumentArray, value);
            IntPtr result = UltralightNative.ulJSFunctionCall(listener.Callback, IntPtr.Zero, argumentArray, new UIntPtr(1), out IntPtr exception);
            if (result != IntPtr.Zero) UltralightNative.ulDestroyJSValue(result);
            if (exception != IntPtr.Zero) UltralightNative.ulDestroyJSValue(exception);
        }
        finally
        {
            Marshal.FreeHGlobal(argumentArray);
            UltralightNative.ulDestroyJSValue(value);
            UltralightNative.ulDestroyJSContext(context);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        if (_backends != null)
            _backends.StreamEventReceived -= OnStreamEvent;
        if (_control != null)
            _control.EventReceived -= OnControlEvent;
        if (_api != IntPtr.Zero && _view != IntPtr.Zero && _attached)
        {
            try { UltralightNative.ulViewDetachJSAPI(_view, _api); } catch { }
        }
        NativeBinding[] bindings;
        lock (_bindingSync)
        {
            bindings = _bindings.Values.ToArray();
            _bindings.Clear();
        }
        foreach (NativeBinding binding in bindings)
        {
            try { UltralightNative.ulDestroyDOMDataBinding(binding.NativeHandle); } catch { }
        }
        if (_dataContextAttached && _view != IntPtr.Zero && _dataContext != IntPtr.Zero)
        {
            try { UltralightNative.ulViewDetachDOMDataContext(_view, _dataContext); } catch { }
        }
        if (_dataContext != IntPtr.Zero)
        {
            try { UltralightNative.ulDestroyDOMDataContext(_dataContext); } catch { }
        }
        if (_api != IntPtr.Zero)
            UltralightNative.ulDestroyJSAPI(_api);
        KeyValuePair<(string BackendId, string StreamId), int>[] leases;
        lock (_streamLeaseSync)
        {
            leases = _streamLeases.ToArray();
            _streamLeases.Clear();
        }
        if (_backends != null)
        {
            Task[] closeTasks = leases.SelectMany(lease => Enumerable.Range(0, lease.Value)
                .Select(_ => _backends.CloseStreamAsync(lease.Key.BackendId, lease.Key.StreamId))).ToArray();
            try { Task.WaitAll(closeTasks, TimeSpan.FromSeconds(1)); } catch { }
        }
        foreach (NativeEventListener listener in _streamListeners)
            UltralightNative.ulDestroyJSValue(listener.Callback);
        _streamListeners.Clear();
        foreach (NativeEventListener listener in _eventListeners)
            UltralightNative.ulDestroyJSValue(listener.Callback);
        _eventListeners.Clear();
        _api = IntPtr.Zero;
        _view = IntPtr.Zero;
        _dataContext = IntPtr.Zero;
        _dataContextAttached = false;
        _attached = false;
    }

    private void OnStreamEvent(BackendEvent value)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        if (value.Name == "stream.data" && value.Data is JsonElement data)
            EmitStreamEvent(value.BackendId, value.StreamId, data, value.Type);
        else if (value.Name == "stream.overflow")
            EmitStreamEvent(value.BackendId, value.StreamId, new { code = "stream_overflow" }, "json");
    }

    private Task<T> InvokeOnOwnerAsync<T>(Func<T> operation)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!_ownerPoster(() =>
            {
                try { completion.TrySetResult(operation()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }))
            {
                completion.TrySetException(new InvalidOperationException("Renderer owner thread is unavailable."));
            }
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private Task InvokeOnOwnerAsync(Action operation) =>
        InvokeOnOwnerAsync(() =>
        {
            operation();
            return true;
        });

    private NativeBinding CreateNativeBinding(string name, string backendId, BackendStreamDescriptor descriptor)
    {
        if (_dataContext == IntPtr.Zero || !_dataContextAttached)
            throw new InvalidOperationException("Data-binding context is not attached.");

        lock (_bindingSync)
        {
            if (_bindings.Values.Any(binding => string.Equals(binding.Name, name, StringComparison.Ordinal)))
                throw new InvalidOperationException("A binding with that name already exists in this view.");
        }

        DomTypePlan plan = BuildDomTypePlan(descriptor.Schema);
        var state = new BindingState(plan);
        GCHandle holderHandle = GCHandle.Alloc(state);
        IntPtr holderState = GCHandle.ToIntPtr(holderHandle);
        IntPtr nativeBinding;
        try
        {
            nativeBinding = UltralightNative.ulDOMDataContextBind(
                _dataContext,
                name,
                plan.NativeType,
                DomDataWalkCallback,
                holderState,
                DomDataAcquireCallback,
                IntPtr.Zero,
                DestroyBindingHolderCallback);
        }
        catch
        {
            FreeBindingHolder(holderState);
            throw;
        }

        if (nativeBinding == IntPtr.Zero)
            throw new InvalidOperationException("Ultralight rejected the data binding.");

        string bindingId = $"binding-{Guid.NewGuid():N}";
        var binding = new NativeBinding(bindingId, name, backendId, descriptor.StreamId, nativeBinding, state);
        lock (_bindingSync)
        {
            if (_bindings.Values.Any(existing => string.Equals(existing.Name, name, StringComparison.Ordinal)))
            {
                UltralightNative.ulDestroyDOMDataBinding(nativeBinding);
                throw new InvalidOperationException("A binding with that name already exists in this view.");
            }
            _bindings.Add(bindingId, binding);
        }
        try
        {
            lock (_streamLeaseSync)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    throw new InvalidOperationException("Renderer view is disposed.");
                (string BackendId, string StreamId) key = (backendId, descriptor.StreamId);
                _streamLeases[key] = _streamLeases.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            if (_backends != null && _backends.TryGetLatestStreamData(backendId, descriptor.StreamId, out JsonElement latest))
            {
                state.SetValue(latest);
                UltralightNative.ulDOMDataContextSync(_dataContext);
            }
        }
        catch
        {
            lock (_bindingSync) _bindings.Remove(bindingId);
            try { UltralightNative.ulDestroyDOMDataBinding(nativeBinding); } catch { }
            throw;
        }
        return binding;
    }

    private void RemoveNativeBinding(NativeBinding binding)
    {
        lock (_bindingSync)
        {
            if (!_bindings.Remove(binding.BindingId, out NativeBinding? current) || !ReferenceEquals(current, binding))
                return;
        }
        try { UltralightNative.ulDestroyDOMDataBinding(binding.NativeHandle); } finally { ReleaseLocalStreamLease(binding.BackendId, binding.StreamId); }
    }

    private void ReleaseLocalStreamLease(string backendId, string streamId)
    {
        lock (_streamLeaseSync)
        {
            (string BackendId, string StreamId) key = (backendId, streamId);
            if (!_streamLeases.TryGetValue(key, out int count))
                return;
            if (count == 1) _streamLeases.Remove(key);
            else _streamLeases[key] = count - 1;
        }
    }

    private void SyncBindingsOnOwner(string? backendId, string streamId, JsonElement data)
    {
        NativeBinding[] bindings;
        lock (_bindingSync)
        {
            bindings = _bindings.Values.Where(binding =>
                Volatile.Read(ref binding.Destroying) == 0 &&
                string.Equals(binding.StreamId, streamId, StringComparison.Ordinal) &&
                (backendId == null || string.Equals(binding.BackendId, backendId, StringComparison.Ordinal))).ToArray();
        }
        if (bindings.Length == 0 || _dataContext == IntPtr.Zero)
            return;
        foreach (NativeBinding binding in bindings)
            binding.State.SetValue(data);
        UltralightNative.ulDOMDataContextSync(_dataContext);
    }

    private static void WalkBinding(IntPtr record, IntPtr instance)
    {
        try
        {
            if (instance == IntPtr.Zero)
                return;
            if (GCHandle.FromIntPtr(instance).Target is BindingState state)
                state.Write(record);
        }
        catch
        {
            // Native callbacks must not cross an exception into Ultralight.
        }
    }

    private static IntPtr AcquireBinding(IntPtr holderState) => holderState;

    private static void DestroyBindingHolder(IntPtr holderState) => FreeBindingHolder(holderState);

    private static void FreeBindingHolder(IntPtr holderState)
    {
        if (holderState == IntPtr.Zero)
            return;
        try
        {
            GCHandle handle = GCHandle.FromIntPtr(holderState);
            if (handle.Target is BindingState state && Interlocked.Exchange(ref state.HolderFreed, 1) != 0)
                return;
            handle.Free();
        }
        catch (InvalidOperationException) { }
    }

    private static DomTypePlan BuildDomTypePlan(JsonSchemaDescriptor schema)
    {
        if (schema.Kind == "null")
            throw new InvalidOperationException("Data bindings do not support null schema nodes.");
        return schema.Kind == "object"
            ? BuildDomObjectPlan(schema.Fields ?? throw new InvalidOperationException("Object schema fields are missing."), false)
            : BuildDomObjectPlan(new Dictionary<string, JsonSchemaDescriptor>(StringComparer.Ordinal) { ["value"] = schema }, true);
    }

    private static DomTypePlan BuildDomRowPlan(JsonSchemaDescriptor schema) =>
        schema.Kind == "object"
            ? BuildDomObjectPlan(schema.Fields ?? throw new InvalidOperationException("Object schema fields are missing."), false)
            : BuildDomObjectPlan(new Dictionary<string, JsonSchemaDescriptor>(StringComparer.Ordinal) { ["value"] = schema }, true);

    private static DomTypePlan BuildDomObjectPlan(IReadOnlyDictionary<string, JsonSchemaDescriptor> fields, bool wrapsScalar)
    {
        IntPtr builder = UltralightNative.ulCreateDOMDataTypeBuilder(null);
        if (builder == IntPtr.Zero)
            throw new InvalidOperationException("Ultralight could not create a data type builder.");

        var entries = new List<DomEntry>(fields.Count);
        try
        {
            uint slot = 0;
            foreach ((string name, JsonSchemaDescriptor schema) in fields)
            {
                if (!IsDomName(name))
                    throw new InvalidOperationException($"Schema field '{name}' is not a valid Ultralight data-binding name.");
                DomEntry entry;
                bool added;
                switch (schema.Kind)
                {
                    case "boolean":
                        added = UltralightNative.ulDOMDataTypeBuilderAddField(builder, name, DomDataBool, 0);
                        entry = new DomEntry(name, schema, slot, DomEntryKind.Field, null);
                        break;
                    case "number":
                        added = UltralightNative.ulDOMDataTypeBuilderAddField(builder, name, DomDataDouble, 0);
                        entry = new DomEntry(name, schema, slot, DomEntryKind.Field, null);
                        break;
                    case "string":
                        added = UltralightNative.ulDOMDataTypeBuilderAddField(builder, name, DomDataString, 0);
                        entry = new DomEntry(name, schema, slot, DomEntryKind.Field, null);
                        break;
                    case "object":
                        DomTypePlan objectPlan = BuildDomObjectPlan(
                            schema.Fields ?? throw new InvalidOperationException("Object schema fields are missing."), false);
                        added = UltralightNative.ulDOMDataTypeBuilderAddObject(builder, name, objectPlan.NativeType, false);
                        entry = new DomEntry(name, schema, slot, DomEntryKind.Object, objectPlan);
                        break;
                    case "array":
                        if (schema.Items == null)
                            throw new InvalidOperationException("Array schema items are missing.");
                        DomTypePlan rowPlan = BuildDomRowPlan(schema.Items);
                        added = UltralightNative.ulDOMDataTypeBuilderAddList(builder, name, rowPlan.NativeType, DomDataNoKey);
                        entry = new DomEntry(name, schema, slot, DomEntryKind.List, rowPlan);
                        break;
                    default:
                        throw new InvalidOperationException($"Data bindings do not support '{schema.Kind}' schema nodes.");
                }
                if (!added)
                    throw new InvalidOperationException($"Ultralight rejected data-binding field '{name}'.");
                entries.Add(entry);
                slot++;
            }

            IntPtr type = UltralightNative.ulDOMDataTypeBuilderBuild(builder);
            if (type == IntPtr.Zero)
                throw new InvalidOperationException("Ultralight could not build the data-binding type.");
            return new DomTypePlan(type, entries.ToArray(), wrapsScalar);
        }
        finally { UltralightNative.ulDestroyDOMDataTypeBuilder(builder); }
    }

    private static bool IsDomName(string value)
    {
        if (string.IsNullOrEmpty(value) || !(value[0] == '_' || IsAsciiLetter(value[0])))
            return false;
        for (int index = 1; index < value.Length; index++)
            if (!(value[index] == '_' || IsAsciiLetter(value[index]) || value[index] is >= '0' and <= '9'))
                return false;
        return true;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool SchemasEqual(JsonSchemaDescriptor left, JsonSchemaDescriptor right)
    {
        if (!string.Equals(left.Kind, right.Kind, StringComparison.Ordinal))
            return false;
        if (left.Kind == "array")
            return left.Items != null && right.Items != null && SchemasEqual(left.Items, right.Items);
        if (left.Kind != "object")
            return true;
        if (left.Fields == null || right.Fields == null || left.Fields.Count != right.Fields.Count)
            return false;
        foreach ((string name, JsonSchemaDescriptor schema) in left.Fields)
            if (!right.Fields.TryGetValue(name, out JsonSchemaDescriptor? other) || !SchemasEqual(schema, other))
                return false;
        return true;
    }

    internal static bool TryParseRuntimeCommand(
        JsonElement input,
        out string commandName,
        out RendererCommand command,
        out string error)
    {
        commandName = "";
        command = new RendererCommand(RendererCommandKind.None);
        error = "";

        if (input.ValueKind != JsonValueKind.Object)
        {
            error = "The runtime command argument must be an object.";
            return false;
        }

        foreach (JsonProperty property in input.EnumerateObject())
        {
            if (property.Name is not ("command" or "group" or "all" or "targetId"))
            {
                error = $"Unknown runtime command property: {property.Name}.";
                return false;
            }
        }

        if (!TryGetString(input, "command", out commandName, out error))
            return false;
        if (!RuntimeCommandNames.TryMap(commandName, out RendererCommandKind kind))
        {
            error = $"Unknown runtime command: {commandName}.";
            return false;
        }

        string? group = null;
        if (input.TryGetProperty("group", out JsonElement groupValue))
        {
            if (groupValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(groupValue.GetString()))
            {
                error = "group must be a non-empty string.";
                return false;
            }
            group = groupValue.GetString()!.Trim();
        }

        bool all = false;
        if (input.TryGetProperty("all", out JsonElement allValue))
        {
            if (allValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                error = "all must be a boolean.";
                return false;
            }
            all = allValue.GetBoolean();
        }

        string? targetId = null;
        if (input.TryGetProperty("targetId", out JsonElement targetValue))
        {
            if (targetValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(targetValue.GetString()))
            {
                error = "targetId must be a non-empty string.";
                return false;
            }
            targetId = targetValue.GetString()!.Trim();
        }

        if (group != null && all)
        {
            error = "group and all cannot be used together.";
            return false;
        }

        if (kind is RendererCommandKind.ShowMenu or RendererCommandKind.HideMenu or RendererCommandKind.ToggleMenu)
        {
            if (group != null || all || targetId != null)
            {
                error = $"{commandName} does not accept group, all, or targetId.";
                return false;
            }
        }
        else if (kind is RendererCommandKind.RevealWindow or RendererCommandKind.HideWindow)
        {
            if (targetId == null)
            {
                error = $"{commandName} requires targetId.";
                return false;
            }
            if (group != null || all)
            {
                error = $"{commandName} does not accept group or all.";
                return false;
            }
        }
        else if (targetId != null)
        {
            error = $"{commandName} does not accept targetId.";
            return false;
        }

        command = new RendererCommand(kind, Group: group, All: all, TargetId: targetId);
        return true;
    }

    private static bool TryGetObject(IntPtr args, UIntPtr argumentCount, out JsonElement value, out string error)
    {
        value = default;
        error = "";
        if (argumentCount.ToUInt64() < 1 || args == IntPtr.Zero)
        {
            error = "One object argument is required.";
            return false;
        }
        IntPtr argument = Marshal.ReadIntPtr(args);
        string? json = ReadJson(argument);
        if (json == null)
        {
            error = "The argument is not JSON-serializable.";
            return false;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "The argument must be an object.";
                return false;
            }
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            error = "The argument JSON is invalid.";
            return false;
        }
    }

    private static bool TryGetBackendId(JsonElement input, out string backendId, out string error) =>
        TryGetString(input, "backendId", out backendId, out error);

    private static bool TryGetMethod(JsonElement input, out string method, out string error)
    {
        if (!TryGetString(input, "method", out method, out error))
            return false;
        method = method.ToUpperInvariant();
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE"))
        {
            error = "method must be GET, POST, PUT, PATCH, or DELETE.";
            return false;
        }
        return true;
    }

    private static bool TryGetPath(JsonElement input, out string path, out string error)
    {
        if (!TryGetString(input, "path", out path, out error))
            return false;
        if (!path.StartsWith("/v1/", StringComparison.Ordinal))
        {
            error = "path must begin with /v1/.";
            return false;
        }
        return true;
    }

    private static bool TryGetString(JsonElement input, string property, out string value, out string error)
    {
        value = "";
        error = "";
        string? candidate = input.TryGetProperty(property, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            error = $"{property} must be a non-empty string.";
            return false;
        }
        value = candidate;
        return true;
    }

    private static string? ReadJson(IntPtr value)
    {
        if (value == IntPtr.Zero)
            return null;
        IntPtr exception;
        IntPtr nativeString = UltralightNative.ulJSValueToJSON(value, 0, out exception);
        if (exception != IntPtr.Zero)
            UltralightNative.ulDestroyJSValue(exception);
        if (nativeString == IntPtr.Zero)
            return null;
        try
        {
            UIntPtr length = UltralightNative.ulStringGetLength(nativeString);
            if (length.ToUInt64() > int.MaxValue)
                return null;
            IntPtr data = UltralightNative.ulStringGetData(nativeString);
            if (data == IntPtr.Zero)
                return "null";
            byte[] bytes = new byte[(int)length.ToUInt64()];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { UltralightNative.ulDestroyString(nativeString); }
    }

    private static IntPtr CreateJsonValue(IntPtr context, string json, out IntPtr exception)
    {
        exception = IntPtr.Zero;
        IntPtr nativeString = UltralightNative.ulCreateString(json);
        if (nativeString == IntPtr.Zero)
            return IntPtr.Zero;
        try { return UltralightNative.ulCreateJSValueFromJSON(context, nativeString, out exception); }
        finally { UltralightNative.ulDestroyString(nativeString); }
    }

    private static IntPtr CreateTypeError(IntPtr context, string message) =>
        UltralightNative.ulCreateJSError(context, JsErrorTypeError, message);

    private enum EventSource
    {
        Stream,
        Cue
    }

    private sealed class NativeEventListener
    {
        public NativeEventListener(string id, IntPtr callback, EventSource? filter)
        {
            Id = id;
            Callback = callback;
            Filter = filter;
        }

        public string Id { get; }
        public IntPtr Callback { get; }
        public EventSource? Filter { get; }
    }

    private sealed class SubscriptionState
    {
        public SubscriptionState(RendererJsBridge bridge, string listenerId)
        {
            Bridge = bridge;
            ListenerId = listenerId;
        }

        public RendererJsBridge Bridge { get; }
        public string ListenerId { get; }
    }

    private sealed class NativeBinding
    {
        public NativeBinding(string bindingId, string name, string backendId, string streamId, IntPtr nativeHandle, BindingState state)
        {
            BindingId = bindingId;
            Name = name;
            BackendId = backendId;
            StreamId = streamId;
            NativeHandle = nativeHandle;
            State = state;
        }

        public string BindingId { get; }
        public string Name { get; }
        public string BackendId { get; }
        public string StreamId { get; }
        public IntPtr NativeHandle { get; }
        public BindingState State { get; }
        public int Destroying;
    }

    private sealed class BindingState
    {
        private readonly DomTypePlan _plan;
        private JsonElement? _value;

        public BindingState(DomTypePlan plan) => _plan = plan;

        public int HolderFreed;

        public void SetValue(JsonElement value) => _value = value.Clone();

        public void Write(IntPtr record)
        {
            if (_value is JsonElement value)
                _plan.Write(record, value);
        }
    }

    private sealed class DomTypePlan
    {
        public DomTypePlan(IntPtr nativeType, DomEntry[] entries, bool wrapsScalar)
        {
            NativeType = nativeType;
            Entries = entries;
            WrapsScalar = wrapsScalar;
        }

        public IntPtr NativeType { get; }
        public DomEntry[] Entries { get; }
        public bool WrapsScalar { get; }

        public void Write(IntPtr record, JsonElement value)
        {
            if (record == IntPtr.Zero)
                return;
            if (WrapsScalar)
            {
                WriteEntry(record, Entries[0], value);
                return;
            }
            if (value.ValueKind != JsonValueKind.Object)
                return;
            foreach (DomEntry entry in Entries)
                if (value.TryGetProperty(entry.Name, out JsonElement child))
                    WriteEntry(record, entry, child);
        }

        private static void WriteEntry(IntPtr record, DomEntry entry, JsonElement value)
        {
            switch (entry.Kind)
            {
                case DomEntryKind.Field:
                    switch (entry.Schema.Kind)
                    {
                        case "boolean":
                            if (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                                UltralightNative.ulDOMDataRecordSetBool(record, entry.Slot, value.GetBoolean());
                            break;
                        case "number":
                            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number))
                                UltralightNative.ulDOMDataRecordSetDouble(record, entry.Slot, number);
                            break;
                        case "string":
                            if (value.ValueKind == JsonValueKind.String)
                            {
                                string text = value.GetString() ?? "";
                                IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(text);
                                try
                                {
                                    UIntPtr length = new((ulong)Encoding.UTF8.GetByteCount(text));
                                    UltralightNative.ulDOMDataRecordSetString(record, entry.Slot, utf8, length);
                                }
                                finally { Marshal.FreeCoTaskMem(utf8); }
                            }
                            break;
                    }
                    break;
                case DomEntryKind.Object:
                    if (value.ValueKind == JsonValueKind.Object && entry.Child != null)
                    {
                        IntPtr childRecord = UltralightNative.ulDOMDataRecordGetChild(record, entry.Slot);
                        entry.Child.Write(childRecord, value);
                    }
                    break;
                case DomEntryKind.List:
                    if (value.ValueKind == JsonValueKind.Array && entry.Child != null)
                    {
                        UltralightNative.ulDOMDataRecordListBegin(
                            record,
                            entry.Slot,
                            new UIntPtr((ulong)value.GetArrayLength()));
                        ulong ordinal = 0;
                        foreach (JsonElement item in value.EnumerateArray())
                        {
                            IntPtr row = UltralightNative.ulDOMDataRecordListRow(
                                record,
                                entry.Slot,
                                new UIntPtr(ordinal++));
                            entry.Child.Write(row, item);
                        }
                        UltralightNative.ulDOMDataRecordListEnd(record, entry.Slot);
                    }
                    break;
            }
        }
    }

    private sealed class DomEntry
    {
        public DomEntry(string name, JsonSchemaDescriptor schema, uint slot, DomEntryKind kind, DomTypePlan? child)
        {
            Name = name;
            Schema = schema;
            Slot = slot;
            Kind = kind;
            Child = child;
        }

        public string Name { get; }
        public JsonSchemaDescriptor Schema { get; }
        public uint Slot { get; }
        public DomEntryKind Kind { get; }
        public DomTypePlan? Child { get; }
    }

    private enum DomEntryKind
    {
        Field,
        Object,
        List
    }

    private sealed record PromiseResult(string? Json, string? Error)
    {
        public static PromiseResult Success(object value) => new(JsonSerializer.Serialize(value, BridgeJsonOptions), null);
        public static PromiseResult Failure(string message) => new(null, message);
    }
}
