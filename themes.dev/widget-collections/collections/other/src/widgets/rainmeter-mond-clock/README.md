# Rainmeter-mond-clock

This is a recreation of the clock from `Mond`, a Rainmeter theme.

## Aether variables

The widget reads these optional top-level Aether variables when it mounts:

```json
{
  "variables": {
    "widget.rainmeter-mond-clock.primary": "#000000",
    "widget.rainmeter-mond-clock.scale": 1
  }
}
```

`primary` accepts a CSS color value. `scale` accepts a positive number and is
multiplied by the widget's default `1.2` visual zoom. Missing or invalid values
use the defaults: black text and scale `1`. Changes take effect when the widget
is mounted or reloaded.
