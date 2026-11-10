# UltraLightWidgetWall

UltraLightWidgetWall hosts JSON-defined web widgets as Windows desktop wallpaper windows using Ultralight AppCore. Each entry in the configuration creates one borderless native window, positions it on a selected monitor, and embeds it behind desktop icons.

## BrowserWall Collection

see [BrowserWall.md](https://github.com/EliteDiamonHawk/browserwall)

## Requirements

- Windows 10/11
- .NET 9 SDK
- Windows x64
- Ultralight SDK `2.0.0-beta.2`

Install the pinned SDK into `third_party\ultralight`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\setup-ultralight.ps1
```

Builds also accept `ULTRALIGHT_SDK_ROOT`.

## Build, test and run

```powershell
dotnet build UltraLightWidgetWall\UltraLightWidgetWall.sln -c Release
dotnet build UltraLightWidgetWall.Tests\UltraLightWidgetWall.Tests.csproj -c Release
dotnet run --project UltraLightWidgetWall.Tests\UltraLightWidgetWall.Tests.csproj -c Release
dotnet run --project UltraLightWidgetWall\UltraLightWidgetWall.csproj -c Release -- --config .\widgets.json
```

## Configuration

Start the application with a required JSON file:

```powershell
dotnet run --project UltraLightWidgetWall\UltraLightWidgetWall.csproj -c Release -- --config .\widgets.json
```

or as standalone executable:

```powershell
UltraLightWidgetWall.exe --config widgets.json
```

Example:

```json
{
  "widgets": [
    {
      "x": 50,
      "y": 50,
      "scaled": true,
      "anchor": "center",
      "width": 25,
      "height": 20,
      "monitor": 1,
      "url": "https://example.com",
      "transparent": true
    }
  ]
}
```

Every widget requires `x`, `y`, `scaled`, `anchor`, `width`, `height`, `monitor`, and `url`. `transparent` is optional and defaults to `true`.

When `scaled` is `true`, position and size values are percentages of the selected monitor from 0 to 100. Otherwise they are screen pixels. Monitor numbers are 1-based and use full monitor bounds, including the taskbar area. Anchors are `top-left`, `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`, or `bottom-right`.

The application reads the configuration at startup. Tray controls can reload the current configuration-derived windows, but the file is not watched automatically.

## Behavior

- One Ultralight window per widget entry.
- Borderless, non-activating desktop wallpaper windows.
- WorkerW/Progman desktop embedding behind desktop icons.
- Per-widget transparency.
- Mouse, keyboard, wheel, modifier, and web-shortcut forwarding.
- Desktop icon selection and context-menu pass-through.
- Fullscreen and manual suspension controls.
- Startup timeout, lifecycle tracking, transactional replacement, and monitor-topology reconciliation.

Malformed configuration, invalid geometry or URLs, unsupported anchors, and unavailable monitors reject the complete replacement while preserving existing windows.

Desktop embedding depends on Windows Shell behavior. Explorer restart recovery, display hotplug/reconfiguration, and platform-specific focus/DPI quirks remain outside current scope.

## License

MIT. See [LICENSE](LICENSE).
