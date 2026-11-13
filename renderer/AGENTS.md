# Renderer contributor guide

## Scope

This directory contains the Windows desktop host for Ultralight-rendered wallpapers, widgets, and start menus. Keep changes inside `renderer/` unless a task explicitly requires repository-level updates.

## Project shape

- `UltraLightRenderer/`: production host and native Windows/Ultralight integration.
- `UltraLightRenderer.Tests/`: lightweight executable test harness.
- `scripts/`: PowerShell wrappers for build, run, publish, and test.
- `third_party/`: vendored or locally installed dependencies. The Ultralight SDK is ignored by Git.
- `docs/`: renderer-specific configuration, operations, architecture, and development notes.
- `docs/configuration/`: configuration guide, JSON Schema, and complete sample configuration.

## Runtime model

- `Program` parses startup options or sends one runtime command.
- `RendererConfiguration` loads one or more JSON files. Each top-level section (`wallpaper`, `widgets`, `start-menu`) may occur in only one file.
- Equal `thread-group` values share one STA thread and one Ultralight `AppCore` instance.
- `RuntimeGroupContext` owns group windows, pumps Ultralight, reconciles native geometry, and handles reload/pause state on its owner thread.
- Wallpapers and widgets embed under the Windows desktop worker window. Start menus remain topmost, non-activating windows managed separately.
- `CommandPipeServer` exposes user-scoped named-pipe control to the already-running instance.

## Change rules

- Preserve x64, `net9.0-windows`, WinForms, STA-thread, and native-handle assumptions.
- Keep all Ultralight and Win32 calls on their owning runtime/group thread unless an existing dispatcher path is used.
- Preserve configuration validation: reject unknown fields, duplicate sections, invalid URLs, invalid monitor numbers, and duplicate logical window IDs.
- When adding config fields, update `RendererConfiguration`, `docs/configuration/example.json`, `docs/configuration/schema.json`, `docs/configuration/configuration.md`, and tests together.
- When adding CLI commands or changing command semantics, update `HostOptions.HelpText`, `docs/runtime-control.md`, and tests together.
- Avoid changing desktop embedding or z-order behavior without validating on Windows with multiple monitors and mixed DPI settings.
- Do not commit SDK binaries, `bin/`, `obj/`, `publish/`, or local configuration containing private URLs or paths.

## Verification

From `renderer/`:

```powershell
.\scripts\test.ps1
.\scripts\build.ps1
```

Parser and policy tests can run without native SDK files:

```powershell
dotnet run --project .\UltraLightRenderer.Tests\UltraLightRenderer.Tests.csproj -p:IncludeUltralight=false
```

For native startup, install the pinned Ultralight SDK under `third_party\ultralight` or pass `-p:UltralightSdkRoot=<path>` / `--ultralight-root <path>` as documented in `docs/development.md`.

## Documentation

Start at [`docs/README.md`](docs/README.md). Keep `docs/configuration/schema.json` and `docs/configuration/example.json` aligned with the parser implementation.
