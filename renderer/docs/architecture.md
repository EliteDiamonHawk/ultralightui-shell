# Architecture

## Process lifecycle

`Program.Main` parses `HostOptions`, handles `--help` or a runtime command, then starts a WinForms `ApplicationContext`. Startup acquires a single-instance lease, loads configuration, resolves windows, starts runtime groups, installs fullscreen and foreground monitors, and creates the tray icon and command pipe.

Shutdown reverses those resources: command server, monitors, tray UI, runtime groups, native windows, and the Ultralight app.

## Runtime groups

Each distinct `thread-group` maps to one `RuntimeGroupHost`. The host owns a background STA thread with a `RuntimeGroupContext` and a WinForms message loop. Aether dispatches Ultralight calls, window creation, destruction, reloads, and group state changes to that owner thread.

`RuntimeGroupContext` runs a 16 ms timer while active. Each tick may:

1. Pump Ultralight.
2. Detect monitor topology changes and reload.
3. Reconcile native geometry at the configured `--reconcile-ms` interval.
4. Recreate windows if a native window disappears unexpectedly.

Paused groups reduce their timer interval to 1000 ms and stop pumping Ultralight.

## Window types

`RendererConfiguration.ResolveWindowSpecs` converts profiles into `RendererWindowSpec` values:

- Wallpaper: virtual-screen (`span`), per-monitor (`all`), or one selected monitor (`single`).
- Widget: monitor-relative geometry resolved from anchor, position scaling, and size scaling.
- Menu: widget-like geometry with visibility and hidden-behavior state.

`UltralightRuntime` creates borderless, hidden windows, panels, and views, loads each URL, then shows each window when ready.

## Native placement

Aether embeds wallpaper and widget windows below the Windows desktop worker window through `DesktopEmbedder`. Aether checks their client bounds and repairs geometry on the reconciliation timer. The menu controller keeps menu windows topmost and shows them without activation.

The host watches monitor topology and foreground-window changes. Fullscreen windows can suspend wallpaper/widget views on their matching monitor. Menu auto-hide ignores foreground windows owned by the renderer process.

## Reload behavior

Reload parses and resolves a candidate configuration before it changes active windows. If validation fails, Aether keeps the current configuration. A group reload replaces that group's windows. Group membership changes require `--reload-all`; that command creates new groups, disposes removed groups, and reloads the remaining groups.

## Native boundary

`UltralightNative` contains the C API bindings. `NativeMethods`, `NativeWindowApi`, and `WinEventApi` contain Win32 interop. Keep ownership and thread affinity explicit when you change these layers. Failures often appear as invalid handles, geometry mismatches, or shutdown races.
