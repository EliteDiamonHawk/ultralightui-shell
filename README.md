# Aether

Aether hosts HTML, CSS, and JavaScript pages as Windows wallpapers, desktop widgets, and a start menu. The renderer uses Ultralight, WinForms, and one STA runtime thread for each configured thread group.

The repository also contains Chronos, the KhaosShell controller. Chronos starts Aether, owns the product tray, restores accepted configuration, and manages the controller-to-renderer channel.

## Repository layout

- [`renderer/`](renderer/): Aether and its renderer documentation.
- [`controller/`](controller/): Chronos and its controller documentation.
- [`renderer/docs/configuration/example.json`](renderer/docs/configuration/example.json): complete renderer example.

## Requirements

- Windows 10/11 x64
- .NET 9 SDK
- Ultralight SDK `2.0.0-beta.2`

Install the pinned SDK under `renderer\third_party\ultralight`, set `ULTRALIGHT_SDK_ROOT`, or pass `-p:UltralightSdkRoot=C:\vendor\ultralight`.

## Build and test

Run the renderer scripts from `renderer/`:

```powershell
Push-Location .\renderer
.\scripts\build.ps1
.\scripts\test.ps1
Pop-Location
```

For parser and policy tests without native SDK files:

```powershell
dotnet run --project .\renderer\Aether.Tests\Aether.Tests.csproj -p:IncludeUltralight=false
```

## Configuration

Pass one or more JSON files with repeated `--config` options. Each top-level section may appear in one file only. The loader accepts `wallpaper`, `widgets`, and `menu`:

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
  "menu": {
    "x": 50,
    "y": 50,
    "pos-scaled": true,
    "size-scaled": false,
    "anchor": "center",
    "width": 800,
    "height": 600,
    "monitor": 1,
    "url": "https://example.com/menu",
    "thread-group": "menu",
    "auto-hide": true,
    "hidden-behavior": "none"
  }
}
```

Omitting `thread-group` uses the section default: `wallpaper`, `widgets`, or `menu`. Equal group names share one Ultralight instance and thread. Different names run in separate groups. Wallpaper profiles support `span`, `all`, and `single` modes, `monitor-urls`, and `window-ids`.

Launch with:

```powershell
Aether.exe --config .\wallpaper.json --config .\widgets.json --config .\menu.json
```

## Runtime control

Read the full configuration reference in [`renderer/docs/configuration/configuration.md`](renderer/docs/configuration/configuration.md). Use [`renderer/docs/configuration/schema.json`](renderer/docs/configuration/schema.json) for editor validation.

Commands communicate with the running user-scoped instance and do not load configuration:

```powershell
Aether.exe --list windows
Aether.exe --show <id>
Aether.exe --hide <id>
Aether.exe --toggle-menu
Aether.exe --toggle-menu show
Aether.exe --toggle-menu hide
Aether.exe --pause desktop
Aether.exe --resume desktop
Aether.exe --status desktop
Aether.exe --reload desktop
Aether.exe --pause-all
Aether.exe --resume-all
Aether.exe --status-all
Aether.exe --reload-all
Aether.exe --exit
```

You may omit the group name from `--pause`, `--resume`, `--status`, and `--reload` when one group runs.

See [`renderer/docs/runtime-control.md`](renderer/docs/runtime-control.md) for startup options, topics, responses, and exit codes. Use [`controller/README.md`](controller/README.md) when Chronos should own the Aether process.

## License

MIT. See `LICENSE` in the source repositories.

The repository includes the original Superswan source under [`renderer/third_party/Superswan`](renderer/third_party/Superswan). Its README describes that older WebView2 host.
