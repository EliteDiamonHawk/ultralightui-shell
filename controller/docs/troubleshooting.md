# Troubleshooting

## Aether not found

Pass `--renderer-path`, place Aether beside Chronos, or install `Aether.exe` at
`%LOCALAPPDATA%\KhaosShell\Aether\installation\Aether.exe`.

## Config not restored

Check `%LOCALAPPDATA%\KhaosShell\Chronos\state.json`. Chronos writes it after Aether
accepts matching paths. Chronos ignores invalid or unsupported state and logs a
diagnostic.

## Control disconnected

The renderer session is no longer usable. Chronos revokes backend grants and
does not reuse inherited handles. Restart KhaosShell to create a fresh session.

## Win key remains normal

The policy is opt-in. Check `settings.json`, startup overrides, hook
diagnostics, and whether another process owns the keyboard-hook policy.
