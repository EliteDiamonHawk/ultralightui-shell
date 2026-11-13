# Architecture

## Process lifecycle

`Program.Main` parses `HostOptions`, handles `--help` or a runtime command, then starts a WinForms `ApplicationContext`. Startup acquires a single-instance lease, loads configuration, resolves windows, starts runtime groups, installs fullscreen and foreground monitors, and creates the tray icon and command pipe.

Shutdown reverses those resources: command server, monitors, tray UI, runtime groups, native windows, and the Ultralight app.

## Runtime groups

Each distinct `thread-group` maps to one `RuntimeGroupHost`. The host owns a background STA thread with a `RuntimeGroupContext` and a WinForms message loop. All Ultralight calls, window creation, destruction, reloads, and group state changes are dispatched to that owner thread.

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
- Start menu: widget-like geometry with visibility and hidden-behavior state.

`UltralightRuntime` creates borderless, hidden windows, panels, and views, loads each URL, then shows each window when ready.

## Native placement

Wallpaper and widget windows are embedded below the Windows desktop worker window through `DesktopEmbedder`. Geometry is checked against actual client bounds and repaired periodically. Start menus use `StartMenuWindowController`, remain topmost, and are shown without activation.

The host watches monitor topology and foreground-window changes. Fullscreen windows can suspend wallpaper/widget views on their matching monitor. Start-menu auto-hide ignores foreground windows owned by the renderer process.

## Reload behavior

Reload first parses and resolves a candidate configuration. If validation fails, the current configuration remains active. A group reload replaces that group’s windows. Group membership changes require `--reload-all`; that command creates new groups, disposes removed groups, then reloads all remaining groups.

## Native boundary

`UltralightNative` contains the C API bindings. `NativeMethods`, `NativeWindowApi`, and `WinEventApi` contain Win32 interop. Keep ownership and thread affinity explicit when changing these layers; failures often surface as invalid handles, geometry mismatch, or shutdown races rather than compile errors.
