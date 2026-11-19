# Persistence

Chronos stores data under `%LOCALAPPDATA%\KhaosShell\`:

- `Chronos\state.json`: schema version, active theme ID, and last successfully
  accepted absolute theme file list.
- `Chronos\themes.json`: registered theme IDs, names, and absolute theme-file
  bundles.
- `logs\`: durable renderer batches and Chronos diagnostics.
- `Chronos\backend-registry.json`: optional backend definitions.

Chronos writes each file through a same-directory temporary file, flushes the
file, and replaces the previous version. Chronos treats missing, malformed, or
unsupported-version files as absent and records a diagnostic. Startup can then
continue. Chronos normalizes theme paths to absolute paths and writes them
only after Aether reports matching active theme. Missing or removed active theme
IDs are not restored. Legacy root-level state
and theme files are not read.

`--restore-state false` skips startup theme-state restoration but does not clear
`Chronos\state.json`. `--tray false` suppresses Chronos tray creation for the
launch. `--no-status-poll` disables only UI timer polling; inherited renderer
events remain active. Current UI toggles are not persisted as next-start
settings.
