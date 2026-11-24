# Minimal example routes

These routes demonstrate how a backend can expose domain behavior without
requiring frontend-specific APIs.

## Health

```text
GET /v1/minimal/health
```

Returns backend identity and protocol version.

## Query state

```text
GET /v1/minimal/value
```

Returns:

```json
{ "value": 20.0 }
```

## Set state

```text
PUT /v1/minimal/value
{ "value": 42.5 }
```

Returns resulting state and publishes it to active `minimal.value`
subscriptions.

## Command

```text
POST /v1/minimal/commands/increment
{ "delta": 1.5 }
```

Returns resulting state and publishes it to active subscriptions.

## Subscribe

Frontend uses its existing generic bridge:

```javascript
const subscription = await window.renderer.openStream({
  backendId: "minimal",
  dataId: "minimal.value",
  type: "json"
});

window.renderer.onStream((event) => {
  if (event.streamId === subscription.streamId) {
    console.log(event.data.value);
  }
});
```

Register the listener before opening the stream when the page must observe the
initial snapshot through raw events. A data binding may instead consume the
same stream through Aether's existing binding API.

The stream is closed with:

```javascript
await window.renderer.closeStream({
  backendId: "minimal",
  streamId: subscription.streamId
});
```

Route names and payloads here are Minimal examples. Future backends define
their own domain routes while retaining the generic request/response and
subscription transport.
