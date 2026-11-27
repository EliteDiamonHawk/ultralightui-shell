# Rainmeter Ageo time and day (modified)

This is a modified recreation of the `Time and Date` widget from `Ageo`, a Rainmeter theme

## Modifications:

The weather function has been stripped out for randomized quotes

## Aether variables

The widget reads these optional top-level Aether variables when it mounts:

```json
{
  "variables": {
    "widget.ageo-day-and-time-modified.primary": "#000000",
    "widget.ageo-day-and-time-modified.accent": "#f4b740",
    "widget.ageo-day-and-time-modified.scale": 1.05
  }
}
```

`primary` and `accent` accept CSS color values. `scale` accepts a positive
number. Missing or invalid values use the defaults: black primary text,
`rgb(255 187 64)` date accent, and `1.05` scale. Changes take effect when the
widget is mounted or reloaded.
