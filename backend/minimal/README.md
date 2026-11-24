# Minimal backend

Minimal backend template for Chronos/Aether-compatible backends. It demonstrates
the generic transport contract, backend-owned routes, subscription-backed
server events, snapshot resync, and latest-state backpressure. It is not a
production backend.

Detailed template documentation:

- [`docs/README.md`](docs/README.md): template overview and lifecycle.
- [`docs/protocol.md`](docs/protocol.md): NDJSON, requests, responses, and streams.
- [`docs/routes.md`](docs/routes.md): sample queries, mutations, and subscriptions.

Build:

```powershell
dotnet build .\backend\minimal\MinimalBackend.csproj
```

Run with Chronos-compatible arguments:

```powershell
.\backend\minimal\bin\Debug\net9.0-windows\win-x64\MinimalBackend.exe `
  --chronos-pipe KhaosShell-Backend-manual `
  --backend-id minimal `
  --protocol 1 `
  --sid <current-user-sid>
```

Chronos supplies the random pipe name and current-user SID in normal use. The
backend writes one readiness JSON line to stdout, then serves UTF-8 NDJSON over
the named pipe. Diagnostics go to stderr. The backend verifies that its process
SID matches `--sid`; the minimal fixture uses Chronos-generated unpredictable
pipe names as its local capability because the .NET client on the supported
Windows host opens local pipe handles anonymously.

Supported transport routes:

```text
POST /v1/request
POST /v1/streams
DELETE /v1/streams/{stream-id}
```

Supported reference routes:

```text
GET /v1/minimal/health
GET /v1/minimal/value

Mutation routes:

```text
PUT  /v1/minimal/value
POST /v1/minimal/commands/increment
```
```

Supported stream:

```text
dataId: minimal.value
type: json
schema: {"kind":"object","fields":{"value":{"kind":"number"}}}
```

`minimal.value` is a subscription-backed state stream. The backend sends a
snapshot after successful subscription, then sends changed values. On a new
pipe/session, opening the subscription again provides resync. Intermediate
state updates may be coalesced for slow consumers; JavaScript owns polling and
may use browser-native WebSockets independently.

Run tests after building the backend:

```powershell
dotnet run --project .\backend\minimal.Tests\MinimalBackend.Tests.csproj --no-restore
```
