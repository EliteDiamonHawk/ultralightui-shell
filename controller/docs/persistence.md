# Persistence

Chronos data lives under `%LOCALAPPDATA%\KhaosShell\`:

- `state.json`: schema version and last successfully accepted absolute config
  file list.
- `settings.json`: schema version and persisted Win-menu toggle preference.
- `logs\`: durable renderer batches and Chronos diagnostics.
- `Chronos\backend-registry.json`: optional backend definitions.

Writes use a same-directory temporary file, flush, and replacement. Missing,
malformed, or unsupported-version files are treated as absent and diagnosed;
startup remains usable. Config paths are normalized to absolute paths and
written only after Aether reports matching active configuration.

`--no-last-config` skips restoration but does not clear `state.json`.
`--no-tray` is process-lifetime state and is not persisted.
