# Troubleshooting

## SDK missing

Symptoms include errors naming `AppCore.dll`, `Ultralight.dll`, `UltralightCore.dll`, `WebCore.dll`, or `resources`.

Check that the SDK is under `renderer\third_party\ultralight`, set `ULTRALIGHT_SDK_ROOT` for the build, or pass `--ultralight-root <path>` when starting Aether. The external root must contain the four DLLs under `bin\` and the `resources\` directory.

## Configuration rejected

Configuration errors identify the section, item, or field. Check these rules:

- the root is an object;
- the file set contains at least one known top-level section;
- each section appears in only one config file;
- widgets and generic windows contain all required geometry fields;
- monitor numbers fit the connected monitor count;
- URLs use HTTP, HTTPS, or `file:`;
- fields and logical IDs are unique.

## Command cannot connect

Runtime commands require a running instance for the same user. Start the host with at least one `--config` first. Avoid combining a command such as `--status-all` with startup options.

If another process owns the instance, inspect and close that process before you
start another copy. Do not delete lock or IPC state; the running process owns
the lease and pipe.

## Widget is misplaced or clipped

Use `--verbose` and inspect `[EmbedCheck]` / `[Window]` output. Confirm:

- monitor numbers match `Screen.AllScreens` order;
- `pos-scaled` and `size-scaled` match intended units;
- anchor is correct;
- width and height are positive;
- mixed-DPI monitor placement is tested on the target machine.

The host periodically reconciles native geometry. A repeated geometry error
points to a Windows desktop-host or DPI interaction rather than a URL problem.

## Top-level window does not remain visible

Check the generic window's `lifecycle.initial` or `lifecycle.initial-visibility` state,
focus policy, and z-order. `pause` pauses a retained view and `unload` destroys
only its active runtime while retaining the definition; `reload` recreates that
specific window. When Chronos owns a menu, Chronos decides when to unload and
reload it.

```powershell
Aether.exe --show <window-id>
Aether.exe --status-all
```

## Reload rejected

Reload validates the complete candidate configuration before changing active
windows. Fix the reported configuration error. If the edit adds or removes a
`runtime-group`, use `--reload-all`.
