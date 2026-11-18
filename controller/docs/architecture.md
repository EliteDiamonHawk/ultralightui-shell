# Architecture

## Startup

`Program` parses controller options, acquires the user-scoped Chronos mutex,
loads persistence and the optional backend registry, resolves Aether, creates
two anonymous pipes, and launches Aether with inherited handle arguments.

Chronos always passes `--no-tray`; the controller owns the product
tray. After Aether accepts its existing `Aether.Commands` pipe, Chronos queries
config, windows, and pipe state. Accepted config paths are then persisted.

## Ownership

| Component | Owns |
| --- | --- |
| Chronos | Aether process, inherited control session, state/settings, tray, Win policy, backend grants, durable log intake |
| Aether | Ultralight runtime groups, native windows, renderer config, existing named command pipe |
| Backend | Private backend protocol and data plane after lazy launch |

Control EOF marks session unavailable and revokes backend grants. Chronos does
not reuse handles or silently attach a later renderer to an old session.

## Command service

Chronos has no public command pipe. Tray and future WinForms use one typed
internal service. It obtains valid renderer IDs, groups, menu presence, and
connection state from Aether inspection output before creating actions.

## Shutdown

Shutdown disables/unhooks Win policy, removes tray UI, requests Aether exit,
revokes backend grants, disposes control streams, and releases the mutex.
