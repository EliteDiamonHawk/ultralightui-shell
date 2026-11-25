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
| `--config <path>` | Optional | JSON config; repeatable. |
| `--no-tray` | Off | Do not construct Aether's tray icon. |
| `--ultralight-root <path>` | Bundled output | External SDK root containing `bin\` and `resources\`. |
| `--reconcile-ms <n>` | `250` | Native window geometry reconciliation interval. |
| `--startup-timeout-ms <n>` | `30000` | Per-window Ultralight readiness timeout. |
| `--verbose` | Off | Embedding and placement diagnostics. |
| `--system-tray [show\|hide]` | Visible | Set tray visibility. Bare flag toggles it. |
| `--help`, `-h` | Off | Print usage. |

The parser makes `--config` paths absolute. Do not combine startup options with runtime commands. Aether can start without configuration, but it then has no windows to manage.

When both inherited controller handles are present, they are the only accepted
startup options. Aether establishes anonymous IPC first and waits for Chronos
to send `POST /v1/renderer/configure`; root, tray policy, and theme/config are
not read from managed-process command-line flags.

## Commands

Commands connect to the already-running user-scoped instance. They do not load config files.

```powershell
Aether.exe --system-tray hide
Aether.exe --system-tray show
Aether.exe --system-tray
Aether.exe --list windows
Aether.exe --list config
Aether.exe --list paths
Aether.exe --show widget-1
Aether.exe --hide widget-1
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

`--hide <id>` and `--show <id>` target one generic window. `--list` accepts `pipes`, `config`, `windows`, or `paths`. `--pause`, `--resume`, `--status`, and `--reload` accept an optional group. The group may be omitted only when exactly one group is running. The `*-all` forms target every group. Menu commands and policy belong to Chronos, which uses the inherited generic window routes.

`--system-tray show` and `--system-tray hide` set system-tray visibility. Bare `--system-tray` toggles visibility. With no running instance, the action controls initial startup visibility; bare toggle starts hidden from the default-visible state.

Standard runtime commands use the user-scoped named pipe `\\.\pipe\Aether.Commands`. CLI flags wrap that command transport. The server accepts one newline-delimited serialized `RendererCommand` per connection. `show` commands return a JSON object; other successful commands return `OK ...`, and failures return `ERR ...`.

The inherited controller channel supports `POST /v1/renderer/configure`,
`GET|POST|PUT|DELETE /v1/renderer/windows`,
`POST /v1/renderer/windows/{id}/actions`, `GET /v1/pipes`, and
`PUT /v1/logging/batch-limits`. Aether has no menu-specific route; Chronos
uses the generic window response for its menu.
Managed Aether emits
`renderer.transport.ready` before configuration and `renderer.ready` after
successful configuration. Other `/v1/*` routes return `404` from Aether.
Chronos handles backend routes and log intake on its side of the channel.

The tray menu exposes the same common actions.

## Responses and exit codes

Successful pipe responses start with `OK`, except for `show` commands, which return a JSON object. Failures start with `ERR` and are written to stderr by the command client.

- `0`: command or startup succeeded.
- `1`: unexpected startup/runtime error.
- `2`: invalid options or invalid renderer configuration.
- `3`: another instance already owns the renderer, or no running instance accepted a command.

The command transport is a local named pipe. It targets the same user and accepts one serialized command per connection. The server limits each UTF-8 frame to 1 MiB. The client bounds its response wait, and a closed pipe returns an error.
