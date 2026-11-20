# Control protocol

Chronos and Aether exchange UTF-8 newline-delimited JSON over two inherited
anonymous pipe handles:

```text
--control-read-handle <uint64>   Chronos -> Aether
--control-write-handle <uint64>  Aether -> Chronos
```

Each frame contains one object and may not exceed 1 MiB. A request uses a UUID
`id`, an uppercase `GET`, `POST`, `PUT`, `PATCH`, or `DELETE` method, and a
`/v1/` path. A response repeats the ID and contains either `body` or a
structured `error`. Requests may complete out of order.

Readers enforce the frame limit while consuming bytes. Oversized frames receive a `400` error with a null ID. Invalid request IDs also produce null-ID errors, so clients do not wait for an unusable correlation ID. Peer EOF fails pending requests and marks the session disconnected.

Managed startup is transport-first. Aether parses only the two handle options,
acquires its single-instance lease, starts this channel, and emits
`renderer.transport.ready`. Chronos then sends `POST /v1/renderer/configure`.
That request carries `ultralightRoot`, `noTray`, optional theme identity, and
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
Chronos theme source and does not cause Aether to read that file. A successful
configuration returns `2xx` and emits `renderer.ready`. Invalid or failed
startup configuration returns a structured error.

Chronos handles:

- `GET /v1/pipes`: control state and secret-free backend snapshots.
- `POST /v1/backends/{backend-id}/connect`: lazy backend startup and grant.
- `DELETE /v1/backends/{backend-id}/connect`: grant release.
- `POST /v1/logs`: durable, redacted renderer event batches.

Aether handles `POST /v1/renderer/configure` for managed startup, reload, and
return-to-idle operations. Aether also emits `renderer.transport.ready` and
`renderer.ready` events.

Chronos sends `PUT /v1/logging/batch-limits` after startup. Request IDs act as
idempotency keys for five minutes or 256 entries. Duplicate content replays;
different content returns `409 request_id_reused`.

The existing Aether named pipe remains the renderer's direct runtime command
transport. Chronos does not expose a second command pipe.

Backend pipe EOF fails pending page requests and removes the stale connection. The next request reconnects through the current grant. Chronos disposes an exited backend before launching its replacement.
