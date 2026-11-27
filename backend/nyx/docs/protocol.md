# Nyx protocol

Chronos launches Nyx with:

```text
--chronos-pipe <name> --backend-id <id> --protocol 1 --sid <sid>
```

Nyx emits one readiness JSON line, then accepts protocol-v1 UTF-8 NDJSON over
the supplied private named pipe. `BackendCodec` reads frames up to 1 MiB. The
server may complete requests out of order.

Generic transport routes:

```text
POST /v1/request
POST /v1/streams
DELETE /v1/streams/{stream-id}
```

Nyx supports the `json` stream type and `nyx.system.metrics` data ID. Metric
stream payloads use a fixed object with a `json` string that contains a
serialized snapshot message. That envelope keeps the stream schema valid while
metric fields remain nullable and dynamic.

Metric events use latest-state delivery: when a client is slower than the
producer, intermediate snapshots may be coalesced. The backend bounds pending
event state per subscription and cancels pending writes when the client or
backend shuts down. The backend pipe denies anonymous clients and verifies the
connected client process SID against the launch SID before serving requests.
