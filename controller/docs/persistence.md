# Persistence

Chronos stores data under `%LOCALAPPDATA%\KhaosShell\`:

- `Chronos\state.json`: schema version, active theme ID, last successfully
  accepted absolute theme file list, runtime state, and an optional previous
  last-known-good fallback theme.
- `Chronos\themes.json`: registered theme IDs, names, and absolute theme-file
  bundles.
- `Chronos\settings.json`: persisted Chronos runtime defaults.
- `logs\`: durable renderer batches and Chronos diagnostics.
- `Chronos\backend-registry.json`: optional backend definitions.

Chronos writes each file through a same-directory temporary file, flushes the
file, and replaces the previous version. At startup, Chronos creates missing
state, theme, settings, and backend-registry files with empty or default values.
Chronos treats malformed and unsupported-version files as absent without
overwriting them, and records diagnostics where applicable. If default-file
creation fails, Chronos keeps the in-memory defaults and continues. Chronos
normalizes theme paths to absolute paths and writes them only after Aether
reports the matching active theme. If Aether accepts a new configuration but
managed-menu creation fails, Chronos unloads Aether to idle and persists the
previous accepted theme only in the fallback fields; it never records the
failed theme as active. A later startup/recovery can retry that fallback through
the normal activation transaction. Chronos does not restore missing or removed
active theme IDs and does not read legacy root-level state or theme files.

Settings contain tray visibility, restore-state behavior, Windows Key Override,
Win-key menu hide/show policy, Panic Escape, keybind rows, status polling,
optional renderer path, and optional Ultralight install root. Panic Escape is
enabled by default. Win-key menu policy defaults to direct hide, no show cue,
and cue names `hide-menu` and `show-menu`. Keybind rows support single-key
chords mapped to ordered cue lists or typed Aether commands. Chronos preserves
cue names in keybinds when the active theme does not declare them. Each row
suppresses matched keystrokes by default; Escape remains reserved for Panic
Escape.
When the Ultralight root is empty, Chronos omits `--ultralight-root` and Aether
uses its own default resolution. The UI writes settings immediately. Explicit
CLI values override persisted values for the current launch. Panic Escape
performs one full Chronos shutdown without opening a confirmation dialog.

If no explicit or restored theme exists, Chronos starts without Aether and
waits for a theme selection. A renderer startup failure leaves the Chronos UI
and theme registry available.

`--restore-state false` skips startup theme-state restoration but does not clear
`Chronos\state.json`. `--tray false` suppresses Chronos tray creation for the
launch. `--status-poll false` disables UI timer polling while inherited renderer
events remain active. Explicit CLI values override persisted settings for the
current launch. UI changes update persisted settings atomically.
