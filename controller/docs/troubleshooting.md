# Troubleshooting

## Aether not found

Pass `--renderer-path`, place Aether beside Chronos, or install `Aether.exe` at
`%LOCALAPPDATA%\KhaosShell\Aether\installation\Aether.exe`.

## Theme not restored

Check `%LOCALAPPDATA%\KhaosShell\Chronos\state.json`. Chronos writes it after Aether
accepts matching paths. Chronos ignores invalid or unsupported state and logs a
diagnostic.

## Control disconnected

The renderer session is no longer usable. Chronos revokes backend grants,
keeps its tray/UI alive, and starts a fresh Aether session when a theme is
applied again.

Pending control requests fail when EOF arrives. Backend requests reconnect on a later call after Aether drops its stale backend pipe.

## Windows Key Override remains disabled

The policy is opt-in. Check `--override-windows-key true`, hook diagnostics,
and whether another process owns the keyboard-hook policy.
