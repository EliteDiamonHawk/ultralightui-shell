# Development

## Requirements

- Windows 10/11 x64.
- .NET 9 SDK.
- Ultralight SDK `2.0.0-beta.2`.

The project targets `net9.0-windows`, x64, and WinForms.

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

The SDK directory is ignored by Git. The project validates these files before build when `IncludeUltralight=true`.

Alternative build-time root:

```powershell
$env:ULTRALIGHT_SDK_ROOT = 'C:\vendor\ultralight'
.\scripts\build.ps1
```

The MSBuild property also works:

```powershell
dotnet build .\UltraLightRenderer.sln -p:UltralightSdkRoot=C:\vendor\ultralight
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

The scripts default to `Release` for build/publish and `Debug` for run/test. Extra arguments are forwarded to `dotnet` or the application as appropriate.

## Test style

The test project is an executable harness, not a test framework project. Add deterministic parser, geometry, policy, or lifecycle checks to `UltraLightRenderer.Tests/Program.cs`. Keep native-window tests separate from tests that can run without an SDK.

Before submitting changes:

1. Run the test script.
2. Run a release build with the SDK available.
3. If native behavior changed, run on Windows with one and multiple monitors, including mixed DPI where available.
4. Exercise start-menu show/hide, pause/resume, status, reload, and exit commands.
