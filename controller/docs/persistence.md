# Persistence

Chronos stores data under `%LOCALAPPDATA%\KhaosShell\`:

- `Chronos\state.json`: schema version, active theme ID, and last successfully
  accepted absolute theme file list.
- `Chronos\themes.json`: registered theme IDs, names, and absolute theme-file
  bundles.
- `Chronos\settings.json`: persisted Chronos runtime defaults.
- `logs\`: durable renderer batches and Chronos diagnostics.
- `Chronos\backend-registry.json`: optional backend definitions.

Chronos writes each file through a same-directory temporary file, flushes the
file, and replaces the previous version. At startup, Chronos creates missing
state, theme, settings, and backend-registry files with empty/default values.
Existing malformed or unsupported-version files are treated as absent and are
not overwritten; malformed-file diagnostics are recorded where applicable. If
default-file creation fails, Chronos keeps the in-memory defaults and continues.
Chronos normalizes
theme paths to absolute paths and writes them only after Aether reports matching
active theme. Missing or removed active theme IDs are not restored. Legacy
root-level state and theme files are not read.

Settings contain tray visibility, restore-state behavior, Windows Key Override,
status polling, optional renderer path, and optional Ultralight install root.
When the Ultralight root is empty, Chronos omits `--ultralight-root` and Aether
uses its own default resolution. UI changes write settings immediately. Explicit
CLI values override them for current launch only.

If no explicit or restored theme exists, Chronos starts without Aether and
waits for a theme selection. Renderer startup failure does not discard the
Chronos UI or theme registry.

`--restore-state false` skips startup theme-state restoration but does not clear
`Chronos\state.json`. `--tray false` suppresses Chronos tray creation for the
launch. `--status-poll false` disables only UI timer polling; inherited renderer
events remain active. Explicit CLI values override persisted settings for the
current launch. UI changes update persisted settings atomically.
