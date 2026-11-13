# Troubleshooting

## SDK missing

Symptoms include errors naming `AppCore.dll`, `Ultralight.dll`, `UltralightCore.dll`, `WebCore.dll`, or `resources`.

Check that the SDK is under `renderer\third_party\ultralight`, set `ULTRALIGHT_SDK_ROOT`, or pass `--ultralight-root <path>` when starting the executable. The external root must contain `bin\` and `resources\`.

## Configuration rejected

Configuration errors identify the section, item, or field. Check:

- root is an object;
- at least one known top-level section exists;
- each section appears in only one config file;
- required widget/start-menu fields are present;
- monitor numbers are within the connected monitor count;
- URLs use HTTP, HTTPS, or `file:`;
- no unknown or duplicate fields exist.

## Command cannot connect

Runtime commands require a running instance for the same user. Start the host with at least one `--config` first. Avoid combining a command such as `--status-all` with startup options.

If a stale process owns the instance, inspect and close that process before starting another copy. Do not delete lock or IPC state blindly; the lease and pipe are process-owned.

## Widget is misplaced or clipped

Use `--verbose` and inspect `[EmbedCheck]` / `[Window]` output. Confirm:

- monitor numbers match `Screen.AllScreens` order;
- `pos-scaled` and `size-scaled` match intended units;
- anchor is correct;
- width and height are positive;
- mixed-DPI monitor placement is tested on the target machine.

The host periodically reconciles native geometry. A repeated geometry error usually indicates a Windows desktop-host or DPI interaction, not a URL problem.

## Start menu does not remain visible

`auto-hide` hides it when another non-owned foreground window activates. `hidden-behavior: pause` suspends its view; `unload` destroys it until `--show` recreates it. Test with:

```powershell
UltraLightRenderer.exe --show
UltraLightRenderer.exe --status-all
```

## Reload rejected

Reload validates the complete candidate configuration before changing active windows. Fix the reported config error. If the edit adds or removes a `thread-group`, use `--reload-all`.
