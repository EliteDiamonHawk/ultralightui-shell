# Nyx storage

Default root:

```text
%LOCALAPPDATA%\KhaosShell\Nyx\settings\
  app.json
  data\
    <encoded-data-id>\<encoded-datatype>.json
```

App settings currently include backend-wide runtime values such as
`hardware_monitor_enabled`. Provider settings use both `dataId` and
`datatype`, for example `nyx.system.metrics` plus `json`.

Files use version `1`, safe encoded identifiers, and atomic temporary-file
replacement. Missing data scopes read as empty objects and are created on the
first write.
