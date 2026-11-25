# Minimal example routes

These routes show how a backend exposes domain behavior through the generic
transport. The frontend does not need backend-specific bridge methods.

## Health

```text
GET /v1/minimal/health
```

The response contains backend identity and protocol version.

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

The response contains the resulting state, and the backend publishes that state
to active `minimal.value` subscriptions.

## Command

```text
POST /v1/minimal/commands/increment
{ "delta": 1.5 }
```

The response contains the resulting state, and the backend publishes it to
active subscriptions.

## Subscribe

The page uses the generic bridge:

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
initial snapshot through raw events. A data binding can consume the same stream
through Aether's existing binding API.

The stream is closed with:

```javascript
await window.renderer.closeStream({
  backendId: "minimal",
  streamId: subscription.streamId
});
```

These route names and payloads belong to Minimal. Other backends define their
own domain routes while retaining the generic request/response and subscription
transport.
