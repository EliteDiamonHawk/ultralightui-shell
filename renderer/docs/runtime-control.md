# Runtime control

Run the executable from its published output directory, or use the PowerShell wrappers from `renderer/`.

## Start

```powershell
UltraLightRenderer.exe --config .\example.json
UltraLightRenderer.exe --config .\wallpaper.json --config .\widgets.json
UltraLightRenderer.exe --config .\example.json --ultralight-root C:\vendor\ultralight
```

Use `--ultralight-root <path>` to load Ultralight from an external installation. The
specified root must contain `bin\AppCore.dll`, `bin\Ultralight.dll`,
`bin\UltralightCore.dll`, `bin\WebCore.dll`, and `resources\`.

Startup options:

| Option | Default | Purpose |
| --- | --- | --- |
| `--config <path>` | Required | JSON config; repeatable. |
| `--ultralight-root <path>` | Bundled output | External SDK root containing `bin\` and `resources\`. |
| `--reconcile-ms <n>` | `250` | Native window geometry reconciliation interval. |
| `--startup-timeout-ms <n>` | `30000` | Per-window Ultralight readiness timeout. |
| `--verbose` | Off | Embedding and placement diagnostics. |
| `--help`, `-h` | Off | Print usage. |

`--config` paths are converted to absolute paths during parsing. Startup options cannot be combined with runtime commands.

## Commands

Commands connect to the already-running user-scoped instance. They do not load config files.

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

`--show` and `--hide` target the configured start menu. `--pause`, `--resume`, `--status`, and `--reload` accept an optional group. The group may be omitted only when exactly one group is running. The `*-all` forms target every group.

The tray menu exposes the same common actions.

## Responses and exit codes

Successful pipe responses start with `OK`. Failures start with `ERR` and are written to stderr by the command client.

- `0`: command or startup succeeded.
- `1`: unexpected startup/runtime error.
- `2`: invalid options or invalid renderer configuration.
- `3`: another instance already owns the renderer, or no running instance accepted a command.

The command transport is a local named pipe. It is intended for same-user control and accepts one serialized command per connection.
