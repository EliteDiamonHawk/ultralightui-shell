# Theme hooks

Chronos reserves top-level `before` and `after` arrays in theme files. Chronos
consumes those sections and removes them before it sends configuration to
Aether. A theme can keep hooks in their own file or mix them with one Aether
configuration section.

```json
{
  "before": [
    {
      "executable": "C:/Tools/wallpaper-helper.exe",
      "arguments": ["--load"],
      "stall": true,
      "exitAfter": true
    },
    "C:/Tools/notify.cmd --theme-loaded"
  ],
  "after": [
    {
      "executable": "C:/Tools/wallpaper-helper.exe",
      "arguments": ["--unload"],
      "waitForExit": true
    }
  ]
}
```

Structured entries support `executable`, optional `arguments`, and these
boolean options. Each defaults to `false`:

- `waitForExit`: wait for this process before the hook phase completes. Later
  hooks may launch first unless `stall` is also true.
- `stall`: wait for this process before launching later hooks.
- `exitAfter`: valid only for `before`; Chronos force-terminates this tracked
  process when the theme changes, unloads, loses the renderer, or shuts down.

String entries run through the Windows command shell and use default options.
Chronos launches hooks in listed order, logs launch failures, and does not
monitor hook processes after launch.

During a theme switch, Chronos completes incoming `before` hooks and Aether
configuration before it runs outgoing `after` hooks. Outgoing `after` hooks
finish the transition. During shutdown, Chronos stops Aether and backends,
terminates tracked `before` processes, then runs active `after` hooks before it
exits.
