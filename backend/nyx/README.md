# Nyx

Nyx is a detached KhaosShell backend for Chronos and Aether. It uses the
protocol in `contracts/BackendContract.cs`, writes one readiness line to
stdout, and serves UTF-8 NDJSON over the private Chronos named pipe.

Nyx provides data and actions only. It has no theme, widget, dashboard, or
static-asset provisioning functions.

Build and test:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
```

Manual launch:

```powershell
.\bin\Debug\net9.0-windows\win-x64\Nyx.exe `
  --chronos-pipe KhaosShell-Backend-manual `
  --backend-id nyx `
  --protocol 1 `
  --sid <current-user-sid>
```

Settings default to `%LOCALAPPDATA%\KhaosShell\Nyx\settings`. Use
`--data-root` or `NYX_DATA_ROOT` for an explicit root, especially in tests.
