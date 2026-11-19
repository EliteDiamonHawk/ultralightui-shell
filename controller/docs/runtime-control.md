# Runtime control

Chronos accepts startup options. It does not expose a second runtime command
pipe.

```powershell
Chronos.exe --config C:\configs\wallpaper.json
Chronos.exe --restore-state false
Chronos.exe --tray true
Chronos.exe --no-status-poll
Chronos.exe --override-windows-key true
```

`--no-status-poll` disables only the WinForms status timer. Inherited Aether
events continue updating the UI. `--restore-state` controls startup theme-state
restoration. `--tray` controls Chronos tray creation. `--override-windows-key`
controls Windows Key Override at startup.

Direct renderer operations use Aether's same-user named pipe:
`\\.\pipe\Aether.Commands`. Use Aether's [`runtime-control.md`](../../renderer/docs/runtime-control.md)
page for renderer commands. The tray and future WinForms operations call the
same internal Chronos command service, which validates current renderer IDs and
groups from inspection snapshots.
