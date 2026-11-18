# Troubleshooting

## Aether not found

Pass `--renderer-path` or install `Aether.exe` at
`%LOCALAPPDATA%\KhaosShell\Aether\installation\Aether.exe`.

## Config not restored

Check `%LOCALAPPDATA%\KhaosShell\state.json`. Chronos writes it only after
Aether accepts matching paths. Invalid or unsupported state is ignored.

## Control disconnected

The renderer session is no longer usable. Chronos revokes backend grants and
does not reuse inherited handles. Restart KhaosShell to create a fresh session.

## Win key remains normal

Policy is opt-in. Check `settings.json`, startup overrides, hook diagnostics,
and whether another process owns the keyboard hook policy.
