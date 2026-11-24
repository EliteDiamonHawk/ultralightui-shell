# Architecture

## Startup

`Program` parses controller options, acquires the user-scoped Chronos mutex,
loads persistence and the optional backend registry, then creates Chronos's
tray/UI state. Chronos always launches an idle-capable Aether session, even
when no theme is selected.

Chronos creates two anonymous pipes and launches Aether with inherited handle
arguments only. Aether establishes that channel before resolving Ultralight or
loading configuration, then emits `renderer.transport.ready`. Chronos sends
root, tray policy, theme identity, and sanitized inline JSON through
`POST /v1/renderer/configure`. Aether reports `renderer.ready` only after
runtime initialization succeeds.

Chronos owns the product tray. Chronos tray creation is controlled by its own
`--tray <true|false>` option. Chronos persists paths only after Aether reports
the matching active configuration.

Theme changes do not restart Aether or either Chronos-to-Aether transport.
Aether swaps its disposable theme runtime while keeping its process, inherited
control channel, and command server alive. Chronos completes incoming `before`
hooks and Aether activation before running outgoing `after` hooks. Unloading
returns Aether to its idle state.

If Aether exits, Chronos ignores late exit events from older sessions. A theme applied after renderer loss starts Aether with its new theme ID and generation.

## Ownership

| Component | Owns |
| --- | --- |
| Chronos | Aether process, inherited control session, state/settings/theme register, tray, global keybind hook, Windows Key Override policy, Escape panic shutdown listener, backend grants, durable log intake |
| Aether | Ultralight runtime groups, native windows, renderer config, existing named command pipe |
| Backend | Private backend protocol and data plane after lazy launch |

Control EOF marks the renderer session unavailable, revokes backend grants,
and keeps Chronos alive so the user can select another theme. Chronos shuts
down Aether only during Chronos shutdown. Theme activation generations fence
stale configuration events and requests without replacing transport handles.

## Command service

Chronos has no public command pipe. Tray, keybind, and WinForms code use one
typed internal service. Keybind cues and Win-key menu policy use the inherited
control channel; typed Aether commands use the existing named command pipe.
Config inspection also returns active-theme cue names for Chronos keybind
editing. That service obtains valid renderer IDs, groups, menu
presence, and connection state from Aether inspection output before it creates
an action. WinForms status uses inherited events plus optional timer polling;
`--status-poll false` disables only timer polling.

## Shutdown

Shutdown disables and unhooks Win policy and Escape panic listener, removes tray UI, requests Aether
exit, waits up to ten seconds for Aether to terminate, then revokes backend
grants, disposes control streams, and releases the mutex. Aether is
force-terminated only if graceful shutdown times out.
