# UltraLightStartMenu

UltraLightStartMenu hosts one JSON-configured web start menu in a borderless Ultralight window. When visible, the window is positioned above normal Windows applications and can be controlled from the tray or command line.

## Requirements

- Windows 10/11
- .NET 9 SDK
- Windows x64
- Ultralight SDK `2.0.0-beta.2`

Install the pinned SDK into `third_party\ultralight`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-ultralight.ps1
```

Builds use the `UltralightSdkRoot` MSBuild property when supplied, then
`ULTRALIGHT_SDK_ROOT`, then `third_party\ultralight`:

```powershell
dotnet build UltraLightStartMenu\csharp\UltraLightStartMenu.sln -c Release `
  -p:UltralightSdkRoot=C:\vendor\ultralight
```

Ultralight is included in output and publish by default. Set `IncludeUltralight=false` to
publish without the native runtime and resources:

```powershell
dotnet publish UltraLightStartMenu\csharp\UltraLightStartMenu.csproj -c Release `
  -p:UltralightSdkRoot=C:\vendor\ultralight `
  -p:IncludeUltralight=false
```

## Build, test, and run

```powershell
dotnet build UltraLightStartMenu\csharp\UltraLightStartMenu.sln -c Release
dotnet run --project UltraLightStartMenu.Tests\csharp\UltraLightStartMenu.Tests.csproj -c Release
dotnet run --project UltraLightStartMenu\csharp\UltraLightStartMenu.csproj -c Release -- --config .\start-menu.json
```

The PowerShell helpers mirror these commands:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\run.ps1 -Arguments @('--config', '.\start-menu.json')
```

## Configuration

Start the application with one required JSON object:

```json
{
  "start-menu": {
    "x": 50,
    "y": 50,
    "pos-scaled": true,
    "size-scaled": false,
    "anchor": "center",
    "width": 800,
    "height": 600,
    "monitor": 1,
    "url": "https://example.com",
    "transparent": true,
    "auto-hide": true
  }
}
```

The required fields are `x`, `y`, `pos-scaled`, `size-scaled`, `anchor`, `width` (or `w`), `height` (or `h`), `monitor`, and `url`. `transparent` defaults to `true`. `auto-hide` also defaults to `true`; set it to `false` to keep the menu visible until explicitly hidden. The legacy `widgets` array is not supported.

When `pos-scaled` is `true`, `x` and `y` are percentages of the selected monitor's width and height; otherwise they are screen pixels. When `size-scaled` is `true`, `width` and `height` are percentages using a uniform scale based on the shorter monitor dimension; otherwise they are pixel dimensions. Monitor numbers are 1-based and use full monitor bounds. Anchors are `top-left`, `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`, or `bottom-right`.

## Tray and command line control

Normal launch:

```powershell
UltraLightStartMenu.exe --config start-menu.json
```

The running instance exposes tray actions to show, hide, reload, print status, and exit. Showing focuses the start menu and raises it as a topmost window. With `auto-hide` enabled, moving focus to another application hides it automatically.

The existing instance can also be controlled without loading the configuration again:

```powershell
UltraLightStartMenu.exe --show
UltraLightStartMenu.exe --hide
```

These commands use a user-scoped named pipe. They return a nonzero exit code if no running instance accepts the command. A second normal launch is rejected rather than creating another tray or start-menu window.

## Behavior

- Exactly one Ultralight window is created from the `start-menu` object.
- The visible window is borderless and `HWND_TOPMOST`.
- The Ultralight page remains loaded while hidden.
- Monitor changes and configured geometry are reconciled while the host runs.
- `transparent` controls rendering transparency.
- Malformed configuration, invalid geometry or URLs, unsupported anchors, and unavailable monitors reject startup or replacement without destroying the current window.

## BrowserWall Collection

See [BrowserWall.md](https://github.com/EliteDiamonHawk/browserwall).

## License

MIT. See [LICENSE](LICENSE).
