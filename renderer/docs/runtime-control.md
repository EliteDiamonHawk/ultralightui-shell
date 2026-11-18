# Runtime control

Run the executable from its published output directory, or use the PowerShell wrappers from `renderer/`.

## Start

```powershell
Aether.exe --config .\example.json
Aether.exe --config .\wallpaper.json --config .\widgets.json
Aether.exe --config .\example.json --ultralight-root C:\vendor\ultralight
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
| `--system-tray [show\|hide]` | Visible | Set tray visibility. Bare flag toggles it. |
| `--help`, `-h` | Off | Print usage. |

`--config` paths are converted to absolute paths during parsing. Startup options cannot be combined with runtime commands.

## Commands

Commands connect to the already-running user-scoped instance. They do not load config files.

```powershell
Aether.exe --reveal-menu
Aether.exe --hide-menu
Aether.exe --system-tray hide
Aether.exe --system-tray show
Aether.exe --system-tray
Aether.exe --show windows
Aether.exe --show config
Aether.exe --show paths
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

`--reveal-menu` and `--hide-menu` target the configured menu. `--hide <id>` and `--reveal <id>` target one window. `--show` accepts `pipes`, `config`, `windows`, or `paths`. `--pause`, `--resume`, `--status`, and `--reload` accept an optional group. The group may be omitted only when exactly one group is running. The `*-all` forms target every group.

`--system-tray show` and `--system-tray hide` set system-tray visibility. Bare `--system-tray` toggles visibility. With no running instance, the action controls initial startup visibility; bare toggle starts hidden from the default-visible state.

Standard runtime commands use the user-scoped named pipe `\\.\pipe\Aether.Commands`. CLI flags are its command-line wrapper. It accepts one newline-delimited serialized `RendererCommand` per connection and returns `OK ...` or `ERR ...`.

The anonymous parent/control pipe is state-only. It supports only `GET /v1/pipes`, returning control-channel state and backend snapshots. Other `/v1/*` routes return `404`.

The tray menu exposes the same common actions.

## Responses and exit codes

Successful pipe responses start with `OK`. Failures start with `ERR` and are written to stderr by the command client.

- `0`: command or startup succeeded.
- `1`: unexpected startup/runtime error.
- `2`: invalid options or invalid renderer configuration.
- `3`: another instance already owns the renderer, or no running instance accepted a command.

The command transport is a local named pipe. It is intended for same-user control and accepts one serialized command per connection.
