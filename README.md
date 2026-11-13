# UltraLightRenderer

UltraLightRenderer creates ultralight engine based wallpapers, desktop widgets, and start menus to support user created html, css, and js based UI. 

## Requirements

- Windows 10/11 x64
- .NET 9 SDK
- Ultralight SDK `2.0.0-beta.2`

Install the pinned SDK into `third_party\ultralight` or set `ULTRALIGHT_SDK_ROOT`. The build also accepts `-p:UltralightSdkRoot=C:\vendor\ultralight`.

## Build and test

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

For configuration and parser tests without native SDK files:

```powershell
dotnet run --project .\UltraLightRenderer.Tests\UltraLightRenderer.Tests.csproj -p:IncludeUltralight=false
```

## Configuration

Startup accepts one or more JSON files. Each top-level section may appear in only one file:

```json
{
  "wallpaper": [
    {
      "url": "https://example.com/wallpaper",
      "mode": "all",
      "thread-group": "desktop",
      "transparent": false
    }
  ],
  "widgets": [
    {
      "x": 50,
      "y": 50,
      "pos-scaled": true,
      "size-scaled": false,
      "anchor": "center",
      "width": 400,
      "height": 200,
      "monitor": 1,
      "url": "https://example.com/widget",
      "thread-group": "desktop",
      "transparent": true
    }
  ],
  "start-menu": {
    "x": 50,
    "y": 50,
    "pos-scaled": true,
    "size-scaled": false,
    "anchor": "center",
    "width": 800,
    "height": 600,
    "monitor": 1,
    "url": "https://example.com/menu",
    "thread-group": "start-menu",
    "auto-hide": true,
    "hidden-behavior": "none"
  }
}
```

Omitting `thread-group` uses the section default: `wallpaper`, `widgets`, or `start-menu`. Equal group names share one Ultralight instance and thread. Different names are isolated. Wallpaper profiles support `span`, `all`, and `single` modes, `monitor-urls`, and `window-ids`.

Launch with:

```powershell
UltraLightRenderer.exe --config .\wallpaper.json --config .\widgets.json --config .\start-menu.json
```

## Runtime control

Commands communicate with the running user-scoped instance and do not load configuration:

```powershell
UltraLightRenderer.exe --show
UltraLightRenderer.exe --hide
UltraLightRenderer.exe --pause desktop
UltraLightRenderer.exe --resume desktop
UltraLightRenderer.exe --status desktop
UltraLightRenderer.exe --reload desktop
UltraLightRenderer.exe --pause-all
UltraLightRenderer.exe --resume-all
UltraLightRenderer.exe --status-all
UltraLightRenderer.exe --reload-all
UltraLightRenderer.exe --exit
```

For a process with exactly one group, the group name may be omitted from `--pause`, `--resume`, `--status`, and `--reload`.

## License

MIT. See `LICENSE` in the source repositories.
