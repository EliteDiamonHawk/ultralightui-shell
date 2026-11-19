# Architecture

## Startup

`Program` parses controller options, acquires the user-scoped Chronos mutex,
loads persistence and the optional backend registry, resolves Aether, creates
two anonymous pipes, and launches Aether with inherited handle arguments.

Chronos always passes Aether's `--no-tray`, so the controller owns the product
tray. Chronos tray creation is controlled by its own `--tray <true|false>`
option.
After Aether accepts its existing `Aether.Commands` pipe, Chronos queries
configuration, windows, and pipe state. Chronos persists paths only after
Aether reports the matching active configuration.

Theme changes do not restart Aether or either Chronos-to-Aether transport.
Aether swaps its disposable theme runtime while keeping its process, inherited
control channel, and command server alive.

## Ownership

| Component | Owns |
| --- | --- |
| Chronos | Aether process, inherited control session, state/settings/theme register, tray, Win policy, backend grants, durable log intake |
| Aether | Ultralight runtime groups, native windows, renderer config, existing named command pipe |
| Backend | Private backend protocol and data plane after lazy launch |

Control EOF marks session unavailable, revokes backend grants, and shuts down
Chronos when Aether was launched by it. Chronos also shuts down when its
managed Aether process exits. Theme activation generations fence stale
configuration events and requests without replacing transport handles.

## Command service

Chronos has no public command pipe. Tray and WinForms code use one typed
internal service. That service obtains valid renderer IDs, groups, menu
presence, and connection state from Aether inspection output before it creates
an action. WinForms status uses inherited events plus optional timer polling;
`--no-status-poll` disables only timer polling.

## Shutdown

Shutdown disables and unhooks Win policy, removes tray UI, requests Aether
exit, waits up to ten seconds for Aether to terminate, then revokes backend
grants, disposes control streams, and releases the mutex. Aether is
force-terminated only if graceful shutdown times out.
