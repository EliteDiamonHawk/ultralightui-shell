# Persistence

Chronos stores data under `%LOCALAPPDATA%\KhaosShell\`:

- `state.json`: schema version and last successfully accepted absolute config
  file list.
- `settings.json`: schema version and persisted Win-menu toggle preference.
- `logs\`: durable renderer batches and Chronos diagnostics.
- `Chronos\backend-registry.json`: optional backend definitions.

Chronos writes each file through a same-directory temporary file, flushes the
file, and replaces the previous version. Chronos treats missing, malformed, or
unsupported-version files as absent and records a diagnostic. Startup can then
continue. Chronos normalizes config paths to absolute paths and writes them
only after Aether reports matching active configuration.

`--no-last-config` skips restoration but does not clear `state.json`.
`--no-tray` is process-lifetime state and is not persisted.
