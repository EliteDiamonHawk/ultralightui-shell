# Control protocol

Chronos and Aether exchange UTF-8 newline-delimited JSON over two inherited
anonymous pipe handles:

```text
--control-read-handle <uint64>   Chronos -> Aether
--control-write-handle <uint64>  Aether -> Chronos
```

Each frame is one object, maximum 1 MiB. Requests use UUID `id`, uppercase
`GET`, `POST`, `PUT`, `PATCH`, or `DELETE`, and a `/v1/` path. Responses repeat
the ID and contain either `body` or structured `error`. Requests correlate
concurrently and may complete out of order.

Chronos handles:

- `GET /v1/pipes`: control state and secret-free backend snapshots.
- `POST /v1/backends/{backend-id}/connect`: lazy backend startup and grant.
- `DELETE /v1/backends/{backend-id}/connect`: grant release.
- `POST /v1/logs`: durable, redacted renderer event batches.

Chronos sends `PUT /v1/logging/batch-limits` after startup. Request IDs are
idempotency keys for five minutes or 256 entries. Duplicate content replays;
different content returns `409 request_id_reused`.

The existing Aether named pipe remains the renderer's direct runtime command
transport. Chronos does not expose a second command pipe.
