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

Chronos handles:

- `GET /v1/pipes`: control state and secret-free backend snapshots.
- `POST /v1/backends/{backend-id}/connect`: lazy backend startup and grant.
- `DELETE /v1/backends/{backend-id}/connect`: grant release.
- `POST /v1/logs`: durable, redacted renderer event batches.

Chronos sends `PUT /v1/logging/batch-limits` after startup. Request IDs act as
idempotency keys for five minutes or 256 entries. Duplicate content replays;
different content returns `409 request_id_reused`.

The existing Aether named pipe remains the renderer's direct runtime command
transport. Chronos does not expose a second command pipe.
