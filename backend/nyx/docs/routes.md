# Nyx routes

All routes are nested inside `POST /v1/request` unless stated otherwise.

## Data

```text
GET /v1/nyx/health
GET /v1/nyx/status
GET /v1/nyx/system/time
GET /v1/nyx/system/metrics
```

Metric request body may contain `fields` as a comma-separated string or string
array. Supported data includes CPU, memory, disks, network, battery-shaped
system data, and gracefully unavailable sensor groups.

## Settings

Global runtime settings:

```text
GET /v1/nyx/settings/app
GET /v1/nyx/settings/app/{key}
PUT /v1/nyx/settings/app/{key}
DELETE /v1/nyx/settings/app/{key}
```

Data/datatype settings:

```text
GET /v1/nyx/settings/data/{dataId}/{datatype}
GET /v1/nyx/settings/data/{dataId}/{datatype}/{key}
PUT /v1/nyx/settings/data/{dataId}/{datatype}/{key}
DELETE /v1/nyx/settings/data/{dataId}/{datatype}/{key}
```

Example:

```text
PUT /v1/nyx/settings/data/nyx.system.metrics/json/interval
5
```

## Actions

```text
POST /v1/nyx/system/controls/sleep
POST /v1/nyx/system/controls/shutdown
POST /v1/nyx/system/controls/lock
POST /v1/nyx/system/controls/logout
POST /v1/nyx/system/controls/restart
```

Only this fixed allowlist is accepted. `NYX_DEV=true` logs actions without
executing them.
