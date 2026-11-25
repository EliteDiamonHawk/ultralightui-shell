# Aether and Chronos

Aether hosts HTML, CSS, and JavaScript pages as Windows wallpapers, desktop
widgets, and generic windows. It uses Ultralight, WinForms, and one STA runtime
thread for each configured thread group.

Chronos controls the KhaosShell session. It starts Aether, owns the product
tray, restores accepted theme state, and manages the controller-to-renderer
channel. Chronos also owns menu policy and creates the menu through Aether's
generic window API.

## Repository layout

- [`renderer/`](renderer/): Aether and its renderer documentation.
- [`controller/`](controller/): Chronos and its controller documentation.
- [`backend/`](backend/): Minimal and Nyx detached backend implementations.
- [`contracts/`](contracts/): shared backend and JavaScript API contracts.
- [`themes.dev/widget-collections/`](themes.dev/widget-collections/): widget
  collections and their build documentation.
- [`renderer/docs/configuration/example.json`](renderer/docs/configuration/example.json): complete renderer example.

## Requirements

- Windows 10/11 x64
- .NET 9 SDK
- Ultralight SDK `2.0.0-beta.2`

The repository includes `global.json`, which selects the .NET 9 SDK for build
and test scripts. Check the active SDK with `dotnet --version`.

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

Run controller tests from `controller/`:

```powershell
Push-Location .\controller
.\scripts\test.ps1
Pop-Location
```

## Standalone configuration

Run Aether directly with one or more JSON files. Repeat `--config` for each
file. Each top-level section may appear in one file only. Standalone Aether
accepts `windows`, `wallpaper`, `widgets`, `variables`, and `cues`.

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
  "variables": {
    "theme": "dark"
  },
  "cues": ["menu.toggle"]
}
```

Omitting `thread-group` uses the section default: `wallpaper`, `widgets`, or
`windows`. Equal group names share one Ultralight instance and thread. Different
names run in separate groups. Wallpaper profiles support `span`, `all`, and
`single` modes, `monitor-urls`, and `window-ids`.

Launch with:

```powershell
Aether.exe --config .\wallpaper.json --config .\widgets.json
```

Use Chronos when the session needs a product tray, theme restoration, keybinds,
backend grants, or a managed menu. Start it with the same theme files:

```powershell
Chronos.exe --config .\wallpaper.json --config .\widgets.json
```

Chronos removes its `before` and `after` hook sections before it sends a
sanitized theme document to Aether. Read [`controller/README.md`](controller/README.md)
for controller startup and persistence rules.

## Runtime control

Read the full configuration reference in [`renderer/docs/configuration/configuration.md`](renderer/docs/configuration/configuration.md). Use [`renderer/docs/configuration/schema.json`](renderer/docs/configuration/schema.json) for editor validation.

Commands communicate with the running user-scoped Aether instance. They do not
load configuration files:

```powershell
Aether.exe --list windows
Aether.exe --list config
Aether.exe --list paths
Aether.exe --show <id>
Aether.exe --hide <id>
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

You may omit the group name from `--pause`, `--resume`, `--status`, and
`--reload` when exactly one group runs. Chronos manages menu visibility through
the generic window API and its inherited control channel.

See [`renderer/docs/runtime-control.md`](renderer/docs/runtime-control.md) for
startup options, commands, responses, and exit codes. Use
[`controller/README.md`](controller/README.md) when Chronos should own the
Aether process.

## License

MIT. See `LICENSE` in the source repositories.

The repository includes the original Superswan source under [`renderer/third_party/Superswan`](renderer/third_party/Superswan). Its README describes that older WebView2 host.
