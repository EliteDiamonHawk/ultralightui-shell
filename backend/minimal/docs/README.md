# Minimal backend template

Minimal is a reference implementation for detached backends launched by
Chronos and consumed by Aether. Copy its transport and lifecycle shape, then
replace its domain state, routes, and stream producers.

Ownership:

- Chronos selects, launches, grants, and stops backend processes.
- Aether resolves backend IDs to private IPC connections and exposes only
  policy-checked logical operations to page JavaScript.
- Backend owns route meanings, domain validation, state, and subscriptions.

Minimal does not define an Aether-specific command API. Page code uses the
generic `request`, `openStream`, and `closeStream` bridge operations.

## Lifecycle

Chronos launches the backend with:

```text
--chronos-pipe <name> --backend-id <id> --protocol 1 --sid <current-user-sid>
```

Backend must:

1. Validate supplied SID against its process identity.
2. Create the private named-pipe server.
3. Write exactly one readiness JSON line to stdout.
4. Serve UTF-8 NDJSON on the pipe.
5. Dispose client-owned subscriptions on disconnect.

Backend payloads never pass through Chronos. Aether connects directly to the
private pipe after receiving a short-lived grant from Chronos.

## Build and test

```powershell
dotnet build .\backend\minimal\minimal\MinimalBackend.csproj
dotnet run --project .\backend\minimal\minimal.Tests\MinimalBackend.Tests.csproj --no-restore
```

See [`protocol.md`](protocol.md) and [`routes.md`](routes.md) before adapting
Minimal for a real backend.
