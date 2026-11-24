# Theme hooks

Chronos reserves top-level `before` and `after` arrays in theme files. These
sections are consumed by Chronos and removed before configuration is sent to
Aether. A theme may keep hooks in their own file or mix them with one Aether
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
boolean options. All default to `false`:

- `waitForExit`: wait for this process before hook phase completes. Later
  hooks may launch first unless `stall` is also true.
- `stall`: wait for this process before launching later hooks.
- `exitAfter`: valid only for `before`; Chronos force-terminates this tracked
  process on theme change, unload, renderer loss, or application shutdown.

String entries run through the Windows command shell and use default options.
Chronos launches hooks in listed order, logs launch failures, and does not
otherwise monitor hook processes.

During a theme switch, incoming `before` hooks and Aether configuration are
completed before outgoing `after` hooks run. Outgoing `after` hooks are the
final transition work. During shutdown, Chronos stops Aether and backends,
terminates tracked `before` processes, then runs active `after` hooks before
exiting.
