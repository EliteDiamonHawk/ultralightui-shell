# Persistence

Chronos stores data under `%LOCALAPPDATA%\KhaosShell\`:

- `Chronos\state.json`: schema version and last successfully accepted absolute config
  file list.
- `Chronos\themes.json`: registered theme names and absolute config-file bundles.
- `settings.json`: schema version and next-start wrapper preferences for tray,
  last-config restoration, and Win-menu toggle.
- `logs\`: durable renderer batches and Chronos diagnostics.
- `Chronos\backend-registry.json`: optional backend definitions.

Chronos writes each file through a same-directory temporary file, flushes the
file, and replaces the previous version. Chronos treats missing, malformed, or
unsupported-version files as absent and records a diagnostic. Startup can then
continue. Chronos normalizes config paths to absolute paths and writes them
only after Aether reports matching active configuration. Legacy root-level state
and theme files are not read.

`--no-last-config` skips restoration but does not clear `Chronos\state.json`.
`--no-tray` is process-lifetime state unless selected as a next-start preference
in KhaosShell UI. `--tray` and `--last-config` override saved next-start
preferences for one launch. `--no-status-poll` disables only UI timer polling;
inherited renderer events remain active.
