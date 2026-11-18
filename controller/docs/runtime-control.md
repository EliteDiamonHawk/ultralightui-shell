# Runtime control

Chronos accepts startup options. It does not expose a second runtime command
pipe.

```powershell
Chronos.exe --config C:\configs\wallpaper.json
Chronos.exe --no-last-config
Chronos.exe --no-tray
Chronos.exe --win-menu-toggle
Chronos.exe --no-win-menu-toggle
```

Direct renderer operations use Aether's same-user named pipe:
`\\.\pipe\Aether.Commands`. Use Aether's [`runtime-control.md`](../../renderer/docs/runtime-control.md)
page for renderer commands. The tray and future WinForms operations call the
same internal Chronos command service, which validates current renderer IDs and
groups from inspection snapshots.
