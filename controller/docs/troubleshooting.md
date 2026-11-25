# Troubleshooting

## Aether not found

Pass `--renderer-path`, place Aether beside Chronos, or install `Aether.exe` at
`%LOCALAPPDATA%\KhaosShell\Aether\installation\Aether.exe`.

## Theme not restored

Check `%LOCALAPPDATA%\KhaosShell\Chronos\state.json`. Chronos writes it after Aether
accepts matching paths. Chronos ignores invalid or unsupported state and logs a
diagnostic.

## Control disconnected

The renderer session is no longer usable. Chronos revokes backend grants, keeps
the tray and UI alive, and starts a fresh Aether session when you apply a theme
again.

Pending control requests fail when EOF arrives. After Aether drops its stale
backend pipe, a later backend request creates a new connection.

## Windows Key Override remains disabled

The policy is opt-in. Check `--override-windows-key true`, hook diagnostics,
and whether another process owns the keyboard-hook policy.
