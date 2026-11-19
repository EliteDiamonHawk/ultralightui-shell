# Runtime control

Chronos accepts startup options. It does not expose a second runtime command
pipe.

```powershell
Chronos.exe --config C:\configs\wallpaper.json
Chronos.exe --no-last-config
Chronos.exe --last-config
Chronos.exe --no-tray
Chronos.exe --tray
Chronos.exe --no-status-poll
Chronos.exe --win-menu-toggle
Chronos.exe --no-win-menu-toggle
```

`--no-status-poll` disables only the WinForms status timer. Inherited Aether
events continue updating the UI. `--tray` and `--last-config` are one-launch
overrides for saved next-start preferences.

Direct renderer operations use Aether's same-user named pipe:
`\\.\pipe\Aether.Commands`. Use Aether's [`runtime-control.md`](../../renderer/docs/runtime-control.md)
page for renderer commands. The tray and future WinForms operations call the
same internal Chronos command service, which validates current renderer IDs and
groups from inspection snapshots.
