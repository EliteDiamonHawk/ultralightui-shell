# Runtime control

Chronos accepts startup options. It does not expose a public runtime command
pipe. Its private inherited channel starts Aether first, then carries the
Ultralight root and initial theme/configuration after readiness.

```powershell
Chronos.exe --config C:\configs\wallpaper.json
Chronos.exe --restore-state false
Chronos.exe --tray true
Chronos.exe --status-poll false
Chronos.exe --override-windows-key true
```

`--status-poll` controls only the WinForms status timer. Inherited Aether events
continue updating the UI. `--restore-state` controls startup theme-state
restoration. `--tray` controls Chronos tray creation. `--override-windows-key`
controls Windows Key Override at startup. Omitted values use persisted settings.

Direct renderer operations use Aether's same-user named pipe:
`\\.\pipe\Aether.Commands`. Use Aether's [`runtime-control.md`](../../renderer/docs/runtime-control.md)
page for renderer commands. The tray and future WinForms operations call the
same internal Chronos command service, which validates current renderer IDs and
groups from inspection snapshots.
