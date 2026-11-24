# Nyx protocol

Chronos launches Nyx with:

```text
--chronos-pipe <name> --backend-id <id> --protocol 1 --sid <sid>
```

Nyx emits one readiness JSON line, then accepts protocol-v1 UTF-8 NDJSON over
the supplied private named pipe. Frames use the shared `BackendCodec`, have a
1 MiB maximum, and may complete out of order.

Generic transport routes:

```text
POST /v1/request
POST /v1/streams
DELETE /v1/streams/{stream-id}
```

Nyx currently supports the `json` stream type and
`nyx.system.metrics` data ID. Metric stream payloads use a fixed object with a
`json` string containing a serialized snapshot message; this keeps the stream
schema valid despite nullable and dynamic metric fields.
