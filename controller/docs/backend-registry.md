# Backend registry

Registry path:

```text
%LOCALAPPDATA%\KhaosShell\Chronos\backend-registry.json
```

If the registry file is missing, Chronos creates it with a default `nyx`
definition pointing to:

```text
%LOCALAPPDATA%\KhaosShell\Nyx\installation\Nyx.exe
```

The WinForms Backends tab can register, update, and remove definitions. New
definitions require an ID and executable path and are applied immediately.
When browsing for `nyx`, the chooser opens in Nyx's installation directory.
Other backend IDs open in `%LOCALAPPDATA%\KhaosShell\backends`; if that
directory is absent, the chooser opens in `%LOCALAPPDATA%\KhaosShell`.

Version-1 shape:

```json
{
  "schemaVersion": 1,
  "backends": {
    "nyx": {
      "executable": "C:\\Users\\user\\AppData\\Local\\KhaosShell\\Nyx\\installation\\Nyx.exe",
      "arguments": [],
      "workingDirectory": null,
      "environment": {}
    },
    "example": {
      "executable": "C:\\Khaos\\ExampleBackend.exe",
      "arguments": [],
      "workingDirectory": "C:\\Khaos",
      "environment": {}
    }
  }
}
```

An undefined ID returns `backend_not_configured`. Chronos starts a defined
backend after the first renderer request. It generates a random private pipe
name, then launches the backend with:

```text
--chronos-pipe <name> --backend-id <id> --protocol 1 --sid <current-user-sid>
```

The backend must create the private named-pipe server with an ACL restricted to
the current user SID when the host pipe stack supports it, and must implement
backend protocol 1. If the host's .NET local client path is anonymous, the
fresh random pipe name becomes the capability, and the backend still verifies
the launch SID. Before Chronos grants access, the backend must write one
readiness JSON line to stdout:

```json
{"ready":true,"protocol":1,"backendId":"example","sid":"S-1-5-21-...","pipeName":"KhaosShell-Backend-..."}
```

Chronos validates readiness, identity, SID, and pipe name within five seconds.
On failure, Chronos stops the backend and returns a structured startup error.
Chronos grants Aether access for five minutes, keeps the pipe name out of page
JavaScript, and does not proxy backend payloads. Chronos revokes the grant on
DELETE, expiry, renderer disconnect, or shutdown.

If a backend exits after startup, Chronos disposes its old process record before
launching the next instance. Aether removes the disconnected backend pipe and
reconnects on the next request.
