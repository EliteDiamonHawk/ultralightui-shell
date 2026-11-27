# Backend protocol template

Minimal implements protocol version 1 from the
[authoritative shared backend contract](../../../contracts/src/RendererContracts/BackendContract.cs).

## Transport

- UTF-8 newline-delimited JSON.
- One complete JSON object per line.
- Maximum frame size: 1 MiB.
- Requests may execute concurrently.
- One serialized writer prevents response/event interleaving.
- Responses correlate by UUID `id` and may arrive out of order.

Request envelope:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "method": "POST",
  "path": "/v1/request",
  "body": {}
}
```

Response envelope:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": 200,
  "body": {},
  "error": null
}
```

Failures use `body: null` and structured `error` fields. Backend routes own
their nested method/path meanings. Aether remains generic and does not know
Minimal route names.

## Subscription streams

Frontend subscribes before backend sends events:

```text
POST /v1/streams
{"dataId":"minimal.value","type":"json"}
```

Successful response returns an opaque stream ID and immutable schema. Backend
then sends an unsolicited event:

```json
{
  "id": null,
  "event": "stream.data",
  "streamId": "stream-id",
  "type": "json",
  "data": { "value": 20.0 }
}
```

The first event is the current snapshot. Future events represent changed
state. `DELETE /v1/streams/{stream-id}` unsubscribes.

## Reconnect and resync

The backend scopes stream IDs to one pipe/session. Disconnect destroys old
subscriptions. A later `openStream` for the same `dataId` creates a new stream
and receives a fresh snapshot. Minimal does not replay event history or accept
resume cursors.

## Backpressure

`minimal.value` is a state stream. Each subscription retains at most one
pending update. A newer value replaces an older pending value. This prevents an
unbounded queue while ensuring a slow consumer eventually receives newest
state. Lossless delivery is not promised for intermediate state values.

This is SSE-style server push over the backend pipe, not HTTP SSE. Minimal does
not implement `text/event-stream`, `EventSource`, or WebSocket proxying. Page
JavaScript owns polling and may open its own browser WebSocket connection.
