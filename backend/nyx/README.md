# Nyx

Nyx is a detached KhaosShell backend for Chronos and Aether. It implements the
shared protocol in [`contracts/src/RendererContracts/BackendContract.cs`](../../contracts/src/RendererContracts/BackendContract.cs),
writes one readiness line to stdout, and serves UTF-8 NDJSON over the private
Chronos named pipe.

Nyx provides data and actions only. It has no theme, widget, dashboard, or
static-asset provisioning functions.

Build and test:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

Manual launch:

```powershell
.\src\Nyx\bin\Debug\net9.0-windows\win-x64\Nyx.exe `
  --chronos-pipe KhaosShell-Backend-manual `
  --backend-id nyx `
  --protocol 1 `
  --sid <current-user-sid>
```

Settings use `%LOCALAPPDATA%\KhaosShell\Nyx\settings` by default. Set
`--data-root` or `NYX_DATA_ROOT` to choose another root in tests or local runs.

See [`docs/protocol.md`](docs/protocol.md), [`docs/routes.md`](docs/routes.md),
and [`docs/storage.md`](docs/storage.md) for the protocol, route, and storage
details.
