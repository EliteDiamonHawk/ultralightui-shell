# Aether and Nyx

Aether is the KhaosShell renderer and exposes `window.renderer` to pages with
bridge access.

The shared bridge calls:

- `GET /v1/nyx/system/time` for synchronized time.
- `GET /v1/nyx/system/metrics` for one metric snapshot.
- `nyx.system.metrics` stream for live metric updates.
- `POST /v1/nyx/system/controls/<name>` for system controls.

Bridge requests use backend ID `nyx`. Manifests request only access needed by
each page. Basic timer pages need no bridge; Basic time, metric, and control
pages demonstrate the corresponding Nyx calls.
