# Architecture

## Startup

`Program` parses controller options, acquires the user-scoped Chronos mutex,
loads persistence and the optional backend registry, resolves Aether, creates
two anonymous pipes, and launches Aether with inherited handle arguments.

Chronos always passes `--no-tray`, so the controller owns the product tray.
After Aether accepts its existing `Aether.Commands` pipe, Chronos queries
configuration, windows, and pipe state. Chronos persists paths only after
Aether reports the matching active configuration.

## Ownership

| Component | Owns |
| --- | --- |
| Chronos | Aether process, inherited control session, state/settings, tray, Win policy, backend grants, durable log intake |
| Aether | Ultralight runtime groups, native windows, renderer config, existing named command pipe |
| Backend | Private backend protocol and data plane after lazy launch |

Control EOF marks the session unavailable and revokes backend grants. Chronos
does not reuse handles or attach a later renderer to an old session.

## Command service

Chronos has no public command pipe. The tray and future WinForms code use one
typed internal service. That service obtains valid renderer IDs, groups, menu
presence, and connection state from Aether inspection output before it creates
an action.

## Shutdown

Shutdown disables and unhooks Win policy, removes tray UI, requests Aether
exit, revokes backend grants, disposes control streams, and releases the
mutex.
