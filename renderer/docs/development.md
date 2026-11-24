# Development

## Requirements

- Windows 10/11 x64.
- .NET 9 SDK.
- Ultralight SDK `2.0.0-beta.2`.

The project targets `net9.0-windows`, x64, and WinForms. Run the scripts from `renderer/`.
The repository `global.json` selects the installed .NET 9 SDK feature band.

The pinned beta.2 package includes `include/Ultralight/CAPI/CAPI_DOMData.h` and
the matching exports used by Aether's `createBinding`/`destroyBinding` bridge.
Bindings support boolean, number, string, object, and array schemas. Null schema
nodes and field names that cannot be represented by Ultralight are rejected.
The current bridge publishes backend values read-only; page edits and DOM data
actions are not forwarded to backends.

## Multiple backends and logical pipe routes

A renderer view may allow multiple backend IDs in its bridge policy. Aether
keeps backend connections, request routing, stream leases, schemas, and latest
stream values scoped by backend ID.

Page JavaScript selects the backend on every operation:

```js
await window.renderer.request({
  backendId: "minimal-a",
  method: "GET",
  path: "/v1/minimal/health"
});

await window.renderer.openStream({
  backendId: "minimal-b",
  dataId: "minimal.value",
  type: "json"
});
```

`openPipe({ backendId })` returns a logical route such as
`renderer://backend/minimal-a`. Aether resolves the actual private named pipe
through Chronos internally; page JavaScript never receives the OS pipe name.
Backend routes use the backend connection. `control(...)` uses the separate
Chronos control channel. Browser JavaScript may use its own WebSocket directly;
Aether does not proxy or manage it.

## SDK layout

Default local SDK location:

```text
renderer/
  third_party/ultralight/
    bin/AppCore.dll
    bin/Ultralight.dll
    bin/UltralightCore.dll
    bin/WebCore.dll
    resources/
```

Git ignores the SDK directory. The project checks these files before a build when `IncludeUltralight=true`.

Alternative build-time root:

```powershell
$env:ULTRALIGHT_SDK_ROOT = 'C:\vendor\ultralight'
.\scripts\build.ps1
```

The MSBuild property also works:

```powershell
dotnet build .\Aether.sln -p:UltralightSdkRoot=C:\vendor\ultralight
```

For parser and policy tests that do not need native SDK files, set `IncludeUltralight=false`.

## Common commands

Run from `renderer/`:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\run.ps1 -Arguments @('--config', '.\example.json', '--verbose')
.\scripts\publish.ps1 -OutputPath .\publish
```

The scripts use `Release` for build/publish and `Debug` for run/test. Extra arguments go to `dotnet` or Aether as the script requires.
The test script disables native Ultralight inputs by default. Pass `-IncludeUltralight` when the SDK is installed. The harness then adds an ABI smoke test that builds a DOM data type, binds it, and synchronizes one value through the pinned C API.

## Test style

The test project runs as an executable harness, not as a test framework project. Add deterministic parser, geometry, policy, or lifecycle checks to `Aether.Tests/Program.cs`. Keep native-window tests separate from tests that can run without an SDK.

Before submitting changes:

1. Run the test script.
2. Run a release build with the SDK available.
3. If native behavior changed, test one and multiple monitors, including mixed DPI where available.
4. Exercise menu show/hide, ID-based hide/show, pause/resume, status, reload, and exit commands.

Pipe changes also require oversized-frame, peer-EOF, pending-request, and reconnect checks in the executable test harness.
