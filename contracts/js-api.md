# Aether JavaScript API

This document describes the communication contract available to theme pages.
It covers page JavaScript, Aether, Chronos, and detached backends.

The bridge carries JSON values. Aether owns the private backend pipe and
Chronos control channel. Theme JavaScript never receives an OS pipe name,
process handle, backend grant, or raw protocol frame.

## 1. Enable the bridge

Add a bridge policy to each wallpaper, widget, or menu page that needs native
communication:

```json
{
  "widgets": [
    {
      "id": "weather",
      "x": 40,
      "y": 40,
      "anchor": "top-left",
      "width": 360,
      "height": 180,
      "monitor": 1,
      "url": "file:///C:/Themes/weather/index.html",
      "bridge": {
        "enabled": true,
        "backend-ids": ["weather"],
        "control-routes": [],
        "runtime-commands": []
      }
    }
  ]
}
```

`backend-ids`, `control-routes`, and `runtime-commands` are allow-lists. An
empty list grants no access for that category. A file URL enables the bridge
by default; network URLs need `"enabled": true`.

Without an attached bridge, `window.renderer` does not exist. Check before
using it when the same page also runs in a normal browser:

```js
const renderer = window.renderer;

if (!renderer) {
  // Browser preview: use mock data or a normal web transport.
}
```

See [`renderer/docs/configuration/configuration.md`](../renderer/docs/configuration/configuration.md)
for the complete configuration format.

## 2. Namespace and shared values

All Aether bindings live below `window.renderer`:

| API | Purpose |
| --- | --- |
| `window.renderer.windowId` | Resolved logical ID for this page window. |
| `window.renderer.pipeName` | Logical route for the default backend. Never an OS pipe name. |
| `window.renderer.capabilities` | Bridge capabilities and policy lists. |
| `window.renderer.openPipe()` | Inspect one allowed backend route. |
| `window.renderer.request()` | Send one request to a backend. |
| `window.renderer.openStream()` | Subscribe to backend state. |
| `window.renderer.closeStream()` | Release a stream subscription. |
| `window.renderer.createBinding()` | Bind backend state to Ultralight DOM data. |
| `window.renderer.destroyBinding()` | Remove a DOM data binding. |
| `window.renderer.control()` | Call an allowed Chronos control route. |
| `window.renderer.aether.variables.get()` | Read configured theme variables. |
| `window.renderer.aether.command()` | Run an allowed Aether runtime command. |
| `window.renderer.onStream()` | Receive legacy stream events. |
| `window.renderer.aether.onEvent()` | Receive stream and cue events. |
| `window.renderer.aether.onCue()` | Receive cue events only. |

`capabilities` looks like this:

```js
console.log(window.renderer.capabilities);
// {
//   request: true,
//   streams: true,
//   bindings: true,
//   variables: true,
//   cues: false,
//   backendIds: ["weather"],
//   controlRoutes: [],
//   runtimeCommands: []
// }
```

The capability flags describe runtime support. The arrays describe policy.
Still handle rejected Promises: a backend can stop, a grant can expire, or a
page can be unloaded while an operation is pending.

## 3. Backend requests from frontend JavaScript

### Inspect a backend route

`openPipe()` is synchronous and takes one object:

```js
const pipe = window.renderer.openPipe({ backendId: "weather" });
// {
//   pipeId: "weather-widget:weather",
//   pipeName: "renderer://backend/weather",
//   backendId: "weather",
//   state: "available"
// }
```

`pipeName` is an Aether logical route. Do not pass it to
`NamedPipeClientStream`, WebSocket, or another browser transport.

### Send a request

`request()` sends a backend-owned route. It returns a Promise for a response
object, even when the backend returns an error status:

```js
async function readWeather() {
  const response = await window.renderer.request({
    backendId: "weather",
    method: "GET",
    path: "/v1/weather/current",
    body: null
  });

  if (response.error || response.status >= 300) {
    throw new Error(response.error?.message ?? `HTTP ${response.status}`);
  }

  return response.body;
}
```

Accepted methods: `GET`, `POST`, `PUT`, `PATCH`, and `DELETE`. Paths must begin
with `/v1/`. `body` may be any JSON value or `null`.

Response shape:

```js
{
  status: 200,
  body: { temperature: 21.5 },
  error: null
}
```

Failure shape:

```js
{
  status: 404,
  body: null,
  error: {
    code: "route_not_found",
    message: "Unknown weather route.",
    details: null
  }
}
```

The Promise rejects for bridge validation, policy, transport, or lifecycle
failures. A backend route failure normally resolves with `error`; inspect both
the resolved response and `catch`.

### Multiple backends

Pass `backendId` on every backend operation. Do not rely on `pipeName` or on
the order of `backend-ids`:

```js
const [weather, music] = await Promise.all([
  window.renderer.request({
    backendId: "weather",
    method: "GET",
    path: "/v1/weather/current"
  }),
  window.renderer.request({
    backendId: "music",
    method: "GET",
    path: "/v1/music/now-playing"
  })
]);
```

Each backend owns its route names and payloads. Aether forwards the generic
request and validates the response envelope; it does not interpret domain
payloads.

## 4. Backend streams

Use a stream for changing state. Use `request()` for one-shot work.

Register the listener before opening the stream when the first snapshot must
be observed through events:

```js
const renderer = window.renderer;
const backendId = "weather";
let subscription;

const stop = renderer.aether.onEvent(event => {
  if (event.source !== "stream" ||
      event.backendId !== backendId ||
      event.streamId !== subscription?.streamId) {
    return;
  }

  if (event.type === "json") {
    renderWeather(event.data);
  }
});

try {
  subscription = await renderer.openStream({
    backendId,
    dataId: "weather.current",
    type: "json"
  });
} catch (error) {
  stop();
  console.error("Could not subscribe to weather", error);
}

async function dispose() {
  stop();
  if (subscription) {
    await renderer.closeStream({
      backendId,
      streamId: subscription.streamId
    });
    subscription = undefined;
  }
}
```

`openStream()` returns:

```js
{
  streamId: "opaque-backend-stream-id",
  type: "json",
  schema: {
    kind: "object",
    fields: { temperature: { kind: "number" } }
  }
}
```

Protocol v1 supports `type: "json"`. The backend schema is immutable for the
stream. Aether validates each event against it before delivery.

`closeStream()` releases one local subscription. Always close streams when a
component or page is disposed. Aether also closes them when the view unloads.

### Stream event forms

Legacy form:

```js
const listenerId = renderer.onStream(event => {
  // { streamId, data, type }
  console.log(event.streamId, event.data);
});
```

`onStream()` returns an internal listener ID. It has no matching public remove
method; the listener ends with the view. Prefer `aether.onEvent()` when the
page needs explicit cleanup or multiple backends.

Unified form:

```js
const stop = renderer.aether.onEvent(event => {
  // Stream: { source: "stream", backendId, streamId, data, type }
  // Cue:    { source: "cue", name, data }
});
stop();
```

`stream.overflow` is delivered as a stream event with
`data: { code: "stream_overflow" }`. It means Aether dropped older queued
updates. State streams should render the newest received value; they should
not assume every intermediate value arrived.

## 5. Read-only DOM data bindings

`createBinding()` opens a JSON stream and exposes its latest value through
Ultralight's DOM data-binding context:

```js
const binding = await renderer.createBinding({
  name: "weatherState",
  backendId: "weather",
  dataId: "weather.current",
  schema: {
    kind: "object",
    fields: {
      temperature: { kind: "number" },
      condition: { kind: "string" }
    }
  }
});

// Use `weatherState` from the page's Ultralight DOM data-binding expressions.
// The binding is read-only and is not a normal writable JS object.

await renderer.destroyBinding({ bindingId: binding.bindingId });
```

Supported binding schema nodes:

```text
boolean                 { "kind": "boolean" }
number                  { "kind": "number" }
string                  { "kind": "string" }
object                  { "kind": "object", "fields": { ... } }
array                   { "kind": "array", "items": { ... } }
```

Null schema nodes are valid for raw streams but not for DOM bindings. Object
fields are closed: every declared field must exist, and undeclared fields are
rejected. Binding field names must be valid Ultralight names: letters, digits,
and underscores, with no leading digit.

The `schema` passed to `createBinding()` must exactly match the schema returned
by the backend. Destroy bindings during component teardown. Destroying a
binding also releases its stream reference.

## 6. Chronos control routes

`control()` calls a route on Chronos, not on a backend:

```js
const status = await window.renderer.control({
  method: "GET",
  path: "/v1/status"
});
```

The page policy must list the exact route in `control-routes`. Methods and
response handling match `request()`. Paths must begin with `/v1/`.

Keep control routes narrow. A route can affect the whole desktop session, so a
theme should request only routes it needs.

## 7. Aether runtime commands

`aether.command()` runs a selected Aether command. The policy must list the
exact command in `runtime-commands`:

```js
await window.renderer.aether.command({ command: "toggleMenu" });
await window.renderer.aether.command({
  command: "hideWindow",
  targetId: "weather"
});
await window.renderer.aether.command({
  command: "pause",
  group: "desktop"
});
```

Supported commands:

| Command | Arguments |
| --- | --- |
| `showMenu`, `hideMenu`, `toggleMenu` | No target fields. |
| `showWindow`, `hideWindow` | Required `targetId`. |
| `pause`, `resume`, `status`, `reload` | Optional `group` or `all: true`, never both. |

The Promise resolves to `{ command, message }`. Invalid, unauthorized, or
failed commands reject.

## 8. Variables and cues

Declare shared values at the top level of the Aether configuration:

```json
{
  "variables": {
    "theme": "dark",
    "refresh-ms": 30000
  },
  "cues": ["menu.toggle", "weather.refresh"]
}
```

Read variables synchronously:

```js
const theme = window.renderer.aether.variables.get("theme");
const refreshMs = window.renderer.aether.variables.get("refresh-ms");
```

Unknown keys return `undefined`. Values keep their JSON types. The primary
argument is a string; `{ key: "theme" }` also works.

Subscribe to declared cues:

```js
const stop = window.renderer.aether.onCue(cue => {
  // { name: "weather.refresh", data: ... }
  if (cue.name === "weather.refresh") refreshWeather();
});
```

`onCue()` returns an unsubscribe function. A cue not listed in the page's
configuration is ignored.

## 9. Backend author contract

A detached backend is an executable launched by Chronos. Aether connects to it
through a private local named pipe. The backend does not communicate with page
JavaScript directly.

Chronos starts the executable with:

```text
--chronos-pipe <pipe-name> --backend-id <id> --protocol 1 --sid <current-user-sid>
```

The backend must write exactly one readiness JSON object as its first stdout
line:

```json
{
  "ready": true,
  "protocol": 1,
  "backendId": "weather",
  "sid": "S-1-5-21-...",
  "pipeName": "KhaosShell-Backend-..."
}
```

Write diagnostics to stderr. After readiness, use UTF-8 newline-delimited JSON
over the pipe. Each frame is one JSON object; maximum frame size is 1 MiB.

### Request handling

Aether sends a generic request envelope:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "method": "POST",
  "path": "/v1/request",
  "body": {
    "method": "GET",
    "path": "/v1/weather/current",
    "body": null
  }
}
```

The backend owns the nested route. Return a correlated response:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": 200,
  "body": { "temperature": 21.5 },
  "error": null
}
```

For failures, set `body` to `null` and return:

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440000",
  "status": 404,
  "body": null,
  "error": {
    "code": "route_not_found",
    "message": "Unknown route.",
    "details": null
  }
}
```

Requests can run concurrently and responses can arrive out of order. Match
responses by `id`. Never return both a non-null `body` and `error`.

### Stream handling

Handle `POST /v1/streams` with body `{ "dataId": "...", "type": "json" }`.
Return an opaque stream ID and immutable schema:

```json
{
  "streamId": "stream-1",
  "type": "json",
  "schema": {
    "kind": "object",
    "fields": { "temperature": { "kind": "number" } }
  }
}
```

Send the current snapshot after subscription succeeds:

```json
{
  "id": null,
  "event": "stream.data",
  "streamId": "stream-1",
  "type": "json",
  "data": { "temperature": 21.5 }
}
```

Handle `DELETE /v1/streams/{streamId}` to release the subscription. Stream IDs
are valid only for their current backend pipe session. After reconnect, Aether
opens new streams; the backend should send a fresh snapshot.

The backend may coalesce pending state updates. It must document when a stream
does not guarantee every intermediate value. It must not emit stream events
before Aether receives a successful subscription response.

See [`BackendContract.cs`](BackendContract.cs),
[`plans/backend-contract.md`](../plans/backend-contract.md), and the
[minimal backend example](../backend/minimal/docs/README.md) for the normative
transport and a working reference.

## 10. Failure and lifecycle rules

- Treat every bridge Promise as fallible.
- Check `response.error` and `response.status`; HTTP-like failures do not always reject.
- Keep backend route and payload definitions in backend documentation.
- Close streams and destroy bindings during page/component teardown.
- Reopen streams after reconnect or theme reload; old stream IDs do not survive.
- Keep backend IDs, control routes, and runtime commands least-privilege.
- Never attempt direct access to Aether's named pipe or Chronos control pipe.

