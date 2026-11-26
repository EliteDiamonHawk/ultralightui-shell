# Architecture

## Startup

`Program` parses controller options, acquires the user-scoped Chronos mutex,
loads persistence and the optional backend registry, then creates Chronos's
tray and UI state. Chronos launches an idle-capable Aether session when it has
an explicit or restored theme; without a theme, Chronos waits for selection.

Chronos creates two anonymous pipes and launches Aether with inherited handle
arguments only. Aether establishes the channel before it resolves Ultralight or
loads configuration, then emits `renderer.transport.ready`. Chronos sends the
root, tray policy, theme identity, and sanitized inline JSON through
`POST /v1/renderer/configure`. Aether emits `renderer.ready` after runtime
initialization succeeds.

Chronos owns the product tray. The `--tray <true|false>` option controls its
creation. Chronos persists paths only after Aether reports
the matching active configuration. A failed post-configuration menu activation
returns Aether to idle; the previous accepted theme may remain as a distinct
last-known-good fallback for a later recovery/startup attempt.

Theme changes keep Aether and both Chronos-to-Aether transports alive. Aether
swaps its disposable theme runtime while keeping its process, inherited control
channel, and command server alive. Chronos completes incoming `before` hooks and
Aether activation before it runs outgoing `after` hooks. Unloading returns
Aether to its idle state.

If Aether exits, Chronos ignores exit events from older sessions. A theme
applied after renderer loss starts Aether with its new theme ID and generation.

## Ownership

| Component | Owns |
| --- | --- |
| Chronos | Aether process, inherited control session, state/settings/theme register, theme transactions and fallback state, tray, global keybind hook, Windows Key Override policy, Escape panic shutdown listener, backend grants, durable log intake |
| Aether | Ultralight runtime groups, native windows, renderer config, existing named command pipe |
| Backend | Private backend protocol and data plane after lazy launch |

Control EOF marks the renderer session unavailable and revokes backend grants.
Chronos stays alive so you can select another theme. Chronos shuts down Aether
only during controller shutdown. Theme activation generations reject stale
configuration events and requests without replacing transport handles.

## Command service

Chronos has no public command pipe. Tray, keybind, and WinForms code use one
typed internal service. Keybind cues, Win-key menu policy, and typed Aether
commands use the inherited control channel for managed sessions.
Managed commands and config inspection use the inherited control channel and
return shared, stable renderer contracts. The named pipe remains available for
standalone/direct Aether CLI commands. Config inspection returns active-theme
cue names for Chronos keybind editing.
Before it creates an action, the service reads valid renderer IDs, groups, menu
presence, and connection state from Aether inspection output. Inherited events
update WinForms status; optional timer polling supplements them, and
`--status-poll false` disables that timer.

## Shutdown

Shutdown disables and unhooks the Windows-key policy and Escape panic listener,
removes tray UI, requests Aether exit, and waits up to ten seconds for Aether to
terminate. Chronos then revokes backend grants, disposes control streams, and
releases the mutex. Chronos force-terminates Aether only after graceful
shutdown times out.
