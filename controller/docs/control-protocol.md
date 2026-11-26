# Control protocol

Chronos and Aether exchange UTF-8 newline-delimited JSON over two inherited
anonymous pipe handles:

```text
--control-read-handle <uint64>   Chronos -> Aether
--control-write-handle <uint64>  Aether -> Chronos
```

Each frame contains one object and may not exceed 1 MiB. A request carries a
UUID `id`, an uppercase `GET`, `POST`, `PUT`, `PATCH`, or `DELETE` method, and a
`/v1/` path. A response repeats the ID and contains either `body` or a
structured `error`. The peers may complete requests out of order.

Readers enforce the frame limit while consuming bytes. The receiver returns a
`400` error with a null ID for an oversized frame. It also returns a null-ID
error for an invalid request ID, so clients do not wait for an unusable
correlation ID. Peer EOF fails pending requests and marks the session
disconnected.

Managed startup begins with transport. Aether parses only the two handle
options, acquires its single-instance lease, starts this channel, and emits
`renderer.transport.ready`. Chronos then sends `POST /v1/renderer/configure`.
That request carries `ultralightRoot`, `noTray`, optional theme identity,
`themeActivationId`, `themeGeneration`, and an opaque `rendererSessionId`, and
one config source:

```json
{
  "protocolVersion": 1,
  "ultralightRoot": "C:\\vendor\\ultralight",
  "noTray": true,
  "config": {
    "mode": "paths",
    "paths": ["C:\\themes\\wallpaper.json"],
    "sourcePaths": ["C:\\themes\\wallpaper.json"]
  }
}
```

`mode` may be `paths`, `json`, or omitted for an idle renderer. JSON mode
contains an object `document` instead of `paths`; `sourcePaths` identifies the
Chronos theme source, but Aether does not read that file. A successful
configuration returns `2xx` and emits `renderer.ready`. Aether returns a
structured error for invalid or failed startup configuration.

Chronos-managed themes use `mode: "json"` with sanitized inline JSON. Chronos
removes its `before` and `after` hook sections, then sends the original theme
paths as `sourcePaths` metadata. Aether's configuration parser never sees the
Chronos-only sections, and Chronos retains theme-file identity.

Chronos handles:

- `GET /v1/pipes`: control state and secret-free backend snapshots.
- `POST /v1/backends/{backend-id}/connect`: lazy backend startup and grant.
- `DELETE /v1/backends/{backend-id}/connect`: grant release.
- `POST /v1/logs`: durable, redacted renderer event batches.

Aether handles `POST /v1/renderer/configure` for managed startup, reload, and
return-to-idle operations. Chronos sends managed runtime commands through
`POST /v1/renderer/command`; inspection uses `GET /v1/renderer/config`,
`GET /v1/renderer/windows`, and `GET /v1/pipes`. Responses use stable shared
renderer/config/window contracts rather than Aether's internal enum values.
Managed Chronos creates and controls its menu as a
generic Aether window with `POST /v1/renderer/windows`,
`GET|PUT|DELETE /v1/renderer/windows/{id}`, and
`POST /v1/renderer/windows/{id}/actions`. The action route supports explicit
`unload` and `reload`; unload retains the definition so the same ID can be
reloaded. Aether also emits
`renderer.transport.ready`, `renderer.ready`, and generic window lifecycle,
visibility, change, focus, `window.unloaded`, and `window.reloaded` events.

Chronos may send unsolicited cue events to Aether:

```json
{"id":null,"event":"cue","body":{"name":"menu.toggle","data":{}}}
```

Aether forwards only cue names that the active configuration declares to
bridge-enabled renderer pages.

Chronos sends `PUT /v1/logging/batch-limits` after startup. Request IDs act as
idempotency keys for five minutes or 256 entries. Duplicate content replays;
different content returns `409 request_id_reused`.

The existing Aether named pipe carries direct standalone renderer runtime
commands. Chronos does not use that pipe for managed theme or policy changes,
and does not expose a second command pipe.

Backend pipe EOF fails pending page requests and removes the stale connection.
The next request reconnects through the current grant. Chronos disposes an
exited backend before launching its replacement.
