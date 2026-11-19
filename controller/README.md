# Chronos controller

Chronos is KhaosShell's Windows controller. It launches one Aether renderer,
owns the inherited control channel, restores accepted theme state, owns the
system tray, and can turn a bare Windows-key tap into a menu toggle.

## Quick start

From `controller/`:

```powershell
.\scripts\run.ps1 -Arguments @('--config', 'C:\configs\wallpaper.json')
```

Startup options:

| Option | Purpose |
| --- | --- |
| `--config <path>` | Forward one configuration path; repeatable. Chronos stores its absolute form. |
| `--restore-state <true\|false>` | Restore persisted theme state; default `true`. |
| `--tray <true\|false>` | Create Chronos tray icon; default `true`. |
| `--no-status-poll` | Disable WinForms timer status polling; event updates remain active. |
| `--override-windows-key <true\|false>` | Enable Windows Key Override; default `false`. |
| `--renderer-path <path>` | Override Aether executable discovery. |

Chronos resolves Aether from `--renderer-path`, beside Chronos, or
`%LOCALAPPDATA%\KhaosShell\Aether\installation\Aether.exe`, in that order.

## Persistent data

```text
%LOCALAPPDATA%\KhaosShell\
  Chronos\state.json
  Chronos\themes.json
  logs\
  Chronos\backend-registry.json
```

Chronos exposes no public command pipe. The existing `Aether.Commands` pipe
remains available for direct renderer control. Tray and WinForms operations use
Chronos's typed command service. Open KhaosShell from tray double-click or its
context menu. Registered themes contain names plus one or more theme file paths.

## Docs

- [`docs/README.md`](docs/README.md): documentation index.
- [`docs/architecture.md`](docs/architecture.md): lifecycle and ownership.
- [`docs/control-protocol.md`](docs/control-protocol.md): inherited NDJSON pipe.
- [`docs/runtime-control.md`](docs/runtime-control.md): startup/runtime command boundary.
- [`docs/persistence.md`](docs/persistence.md): state and theme-register rules.
- [`docs/backend-registry.md`](docs/backend-registry.md): lazy backend launch.
- [`docs/development.md`](docs/development.md): build, test, and publish.
- [`docs/troubleshooting.md`](docs/troubleshooting.md): common failures.
