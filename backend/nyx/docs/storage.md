# Nyx storage

Default root:

```text
%LOCALAPPDATA%\KhaosShell\Nyx\settings\
  app.json
  data\
    <encoded-data-id>\<encoded-datatype>.json
```

App settings include backend-wide runtime values such as
`hardware_monitor_enabled`. Provider settings use both `dataId` and
`datatype`, such as `nyx.system.metrics` and `json`.

Nyx writes version `1` files with safe encoded identifiers and atomic
temporary-file replacement. A missing data scope reads as an empty object; Nyx
creates the file on its first write.
