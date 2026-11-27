# KhaosShell end4-pc-remake Widgets

This collection contains 26 end4-pc-remake widget references. Build it from the
workspace root:

    pnpm widget:verify --collection end4-pc-remake
    pnpm collection:build --collection end4-pc-remake

Each widget owns its markup, styling, behavior, manifest, and README. Workspace
packages provide shared runtime code and assets.

See the workspace [README](../../README.md) and
[shared documentation](../../docs/DOCUMENTATION.md).

## Behavior settings

Behavior settings can be supplied as mount-time options, or as Aether
variables using the key pattern:

    widget.end4-pc-remake.<widget-id>.<setting>

Mount-time options take precedence over Aether variables. The widget manifests
contain the authoritative types, defaults, ranges, and format tokens.

### Calendars and dates

- `week`: `date`, `locale`, `weekStartsOn`, `daysBefore`, `daysAfter`,
  `highlightDate`, `updateInterval`
- `month`: `date`, `locale`, `weekStartsOn`, `highlightDate`, `lockDay`,
  `maxWeeks`, `updateInterval`
- `date`: `date`, `locale`, `highlightDate`, `updateInterval`
- `single-line-date`: `date`, `locale`, `format`, `highlightDate`,
  `updateInterval`

Calendar dates use `YYYY-MM-DD`. `lockDay` is an object with a zero-based
column `x` and row `y`.

### Clocks

- `digital-clock`: `format`, `seconds`, `showPeriod`, `syntax`,
  `offsetMinutes`, `updateInterval`
- `digital-and-analog-clock`: the digital-clock settings plus `smooth`
- `analog-clock` and `radial-clock`: `offsetMinutes`, `smooth`,
  `updateInterval`
- `pixel-clock`: `format`, `offsetMinutes`, `updateInterval`

Clock offsets are fixed numeric minute offsets. IANA timezone names are not
supported in this version. Clock and calendar refresh intervals are limited to
1–2147483 seconds so their millisecond timers remain within the host limit.

### Timers

- `countdown`: `time`, `syntax`, `label`, `buttons`
- `stopwatch`: `limit`, `syntax`, `label`, `buttons`

Countdown `time` and stopwatch `limit` are milliseconds. Countdown `time` must
be between zero and one hour, and stopwatch limits to 59:59. Timer syntax uses
the shared duration tokens, such as `MM:ss` or `mm:ss`.

### System metrics

- `battery-percentage`: `format`, `digitLimit`, `label`, `updateInterval`
- `cpu-core-count`: `coreType`, `format`, `digitLimit`, `label`,
  `updateInterval`
- `cpu-frequency`: `core`, `digits`, `format`, `digitLimit`, `label`,
  `updateInterval`
- `cpu-utilization`: `core`, `format`, `digitLimit`, `label`,
  `updateInterval`
- `disk-usage`: `mountPoint`, `format`, `digitLimit`, `label`,
  `updateInterval`
- `gpu-temperature`: `sensorId`, `gpuId`, `format`, `digitLimit`, `label`,
  `updateInterval`
- `last-boot-time`: `format`, `digitLimit`, `label`, `updateInterval`
- `memory-utilization`: `format`, `digitLimit`, `label`, `updateInterval`
- `network-received`: `format`, `digitLimit`, `label`, `updateInterval`
- `swap-utilization`: `metric`, `format`, `digitLimit`, `label`,
  `updateInterval`

Metrics use the fixed Nyx bridge. Endpoint, transport, backend selection, and
callback wiring remain internal. Network rates are derived from successive Nyx
byte-counter snapshots. Selector values in widget README examples, such as a
disk mount point or GPU id, are examples and must match the host’s Nyx data.
Nyx currently provides logical CPU core count and overall CPU utilization in
this collection. Physical core count, per-core utilization, and CPU frequency
fields are not currently available from the backend, so those views display
`--` until Nyx supplies the requested data; their selectors remain available
for compatible or future backend data. Nyx metric refresh intervals are
restricted to 1–3600 seconds. When a widget’s primary metric is unavailable,
the complete value display is `--`, including when its configured format would
otherwise add units. CPU core, CPU frequency, CPU utilization, and swap
formats have conditional defaults based on their selector settings; the
runtime resolves those defaults from the selected mode.

Invalid explicit mount-time options throw. Invalid Aether values fall back to
their manifest defaults and are recorded on the document as configuration
warnings.
