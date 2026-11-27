# Development

## Requirements

- Windows 10/11 x64.
- .NET 9 SDK.
- Ultralight SDK `2.0.0-beta.2`.

The project targets `net9.0-windows`, x64, and WinForms. Run the scripts from
`renderer/`. The repository's `global.json` selects the .NET 9 SDK feature
band.

The pinned beta.2 package includes `include/Ultralight/CAPI/CAPI_DOMData.h` and
the matching exports used by Aether's `createBinding`/`destroyBinding` bridge.
Bindings support boolean, number, string, object, and array schemas. Ultralight
rejects null schema nodes and field names that it cannot represent.
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
`renderer://backend/minimal-a`. Aether resolves the private named pipe through
Chronos; page JavaScript does not receive the OS pipe name. Backend routes use
the backend connection. `control(...)` uses the separate Chronos control
channel. Browser JavaScript can use its own WebSocket; Aether does not proxy or
manage it.

Bridge-enabled pages also receive shared configuration and Chronos cues:

```js
const refreshMs = window.aether.variables.get("refresh-ms");
const stop = window.aether.onEvent(event => {
  if (event.source === "cue") console.log(event.name, event.data);
});
```

`window.aether.onCue(callback)` is the cue-only form. The top-level `cues`
section must declare each cue name. `window.renderer.onStream(callback)` remains
available for the existing stream payload shape.

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

Git ignores the SDK directory. The project checks these files before a build
when `IncludeUltralight=true`.

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

The scripts use `Release` for build and publish and `Debug` for run and test.
Each script forwards extra arguments to `dotnet` or Aether as documented. The
test script disables native Ultralight inputs by default. Pass
`-IncludeUltralight` when the SDK is installed; the harness then adds an ABI
smoke test that builds a DOM data type, binds it, and synchronizes one value
through the pinned C API.

## Test style

The test project runs as an executable harness. Add deterministic parser,
geometry, policy, or lifecycle checks to `tests/Aether.Tests/Program.cs`. Keep
native-window tests separate from tests that can run without an SDK.

Before submitting changes:

1. Run the test script.
2. Run a release build with the SDK available.
3. If native behavior changed, test one and multiple monitors, including mixed
   DPI where available.
4. Exercise generic window show/hide, focus, ID-based actions, pause/resume, status, reload, and exit commands. Chronos-specific menu behavior is tested from the controller.

Pipe changes also require oversized-frame, peer-EOF, pending-request, and reconnect checks in the executable test harness.

## Native image validation matrix

Native image rendering uses one STA-owned DirectComposition runtime per runtime
group. Keep creation, drawing, pause/resume, reload, and disposal on that owner
thread; cross-thread commands must use the runtime group's dispatcher.

When a Windows desktop and graphics device are available, validate the native
image path with both hardware rendering and the existing WARP fallback. Use
animated images with small subframes away from the origin and compare updates
with a full redraw under `contain`, `cover`, and `stretch`, including rotation
and transparent pixels. The first draw must cover the complete surface.

Also repeat create/destroy/reload and shutdown cycles while checking that HWNDs
are destroyed and native resource counts remain stable. Repeat the scenarios on
one monitor and multiple monitors, move windows across mixed-DPI monitors,
resize/reload them, and record unavailable desktop, GPU, device-loss, or DPI
checks explicitly rather than treating them as passing.
