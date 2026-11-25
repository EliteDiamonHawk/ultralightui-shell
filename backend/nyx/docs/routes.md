# Nyx routes

Clients send these routes inside `POST /v1/request`, unless a section says
otherwise.

## Data

```text
GET /v1/nyx/health
GET /v1/nyx/status
GET /v1/nyx/system/time
GET /v1/nyx/system/metrics
```

The metric request body may contain `fields` as a comma-separated string or a
string array. Nyx exposes CPU, memory, disk, network, and battery-shaped system
data. It returns an unavailable value for sensor groups that the host cannot
provide.

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

Nyx accepts only this fixed allowlist. `NYX_DEV=true` logs actions without
executing them.
