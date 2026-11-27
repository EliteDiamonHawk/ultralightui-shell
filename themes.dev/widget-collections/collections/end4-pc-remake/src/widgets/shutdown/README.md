# shutdown

## Aether variables

The widget reads optional top-level Aether variables when it mounts:

```json
{
  "variables": {
    "widget-collection.end4-pc-remake.primary": "#d6d6d6",
    "widget-collection.end4-pc-remake.secondary": "#2e2e2e",
    "widget.end4-pc-remake.shutdown.primary": "#ffffff",
    "widget.end4-pc-remake.shutdown.secondary": "#202020",
    "widget.end4-pc-remake.shutdown.scale": 1.1
  }
}
```

Widget-specific `primary` and `secondary` values override collection values.
Missing or invalid colors use the widget family defaults. Scale is widget-only,
must be positive, and otherwise uses the widget default. Changes take effect
when the widget is mounted or reloaded.
