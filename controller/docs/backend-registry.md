# Backend registry

Registry path:

```text
%LOCALAPPDATA%\KhaosShell\Chronos\backend-registry.json
```

Version-1 shape:

```json
{
  "schemaVersion": 1,
  "backends": {
    "nyx": {
      "executable": "C:\\Khaos\\Nyx.exe",
      "arguments": [],
      "workingDirectory": "C:\\Khaos",
      "environment": {}
    }
  }
}
```

An undefined ID returns `backend_not_configured`. Chronos starts a defined
backend after the first renderer request. It generates a random private pipe
name and launches the backend with:

```text
--chronos-pipe <name> --backend-id <id> --protocol 1 --sid <current-user-sid>
```

The backend must create the private named-pipe server with an ACL restricted to
the current user SID and implement backend protocol 1. Before Chronos grants
access, the backend must write one readiness JSON line to stdout:

```json
{"ready":true,"protocol":1,"backendId":"nyx","sid":"S-1-5-21-...","pipeName":"KhaosShell-Backend-..."}
```

Chronos validates readiness, identity, SID, and pipe name within five seconds.
A failure stops the backend and returns a structured startup error. Chronos
returns a five-minute grant to Aether, keeps the pipe name out of page
JavaScript, and does not proxy backend payloads. Chronos revokes grants on
DELETE, expiry, renderer disconnect, or shutdown.
