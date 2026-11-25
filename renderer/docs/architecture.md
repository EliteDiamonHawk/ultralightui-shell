# Architecture

## Process lifecycle

`Program.Main` parses `HostOptions`, handles `--help` or a runtime command, then
starts a WinForms `ApplicationContext`. Startup acquires a single-instance
lease before managed transport setup. With inherited handles, Aether starts the
anonymous control channel and waits for Chronos configuration before it resolves
Ultralight, loads configuration, or creates runtime groups. Standalone startup
uses direct CLI configuration. A managed control disconnect exits Aether.

Shutdown disposes those resources in reverse order: the command server,
monitors, tray UI, runtime groups, native windows, and the Ultralight app.

## Runtime groups

Each distinct `thread-group` maps to one `RuntimeGroupHost`. The host owns a background STA thread with a `RuntimeGroupContext` and a WinForms message loop. Aether dispatches Ultralight calls, window creation, destruction, reloads, and group state changes to that owner thread.

`RuntimeGroupContext` runs a 16 ms timer while active. Each tick can:

1. Pump Ultralight.
2. Detect monitor topology changes and reload.
3. Reconcile native geometry at the configured `--reconcile-ms` interval.
4. Recreate windows if a native window disappears unexpectedly.

Paused groups reduce their timer interval to 1000 ms and stop pumping Ultralight.

Configuration swaps use one host lock. Control-pipe and command-pipe requests cannot rotate theme scope at the same time.

## Window types

`RendererConfiguration.ResolveWindowSpecs` converts configuration into `RendererWindowSpec` values:

- Generic windows: explicit monitor/work-area/virtual-screen geometry, placement,
  z-order, input/focus, lifecycle, notification, and bridge policies.
- Wallpaper: virtual-screen (`span`), per-monitor (`all`), or one selected monitor (`single`) desktop adapter.
- Widget: monitor-relative geometry resolved from anchor, position scaling, and size scaling desktop adapter.

`UltralightRuntime` creates borderless hidden windows, panels, and views, loads
each URL, then shows each window when ready.

## Native placement

Aether embeds desktop-placement windows below the Windows desktop worker window
through `DesktopEmbedder`. The generic placement controller applies frame,
taskbar, z-order, focus, and work-area policy to top-level windows. Aether
checks geometry and repairs it on the reconciliation timer.

The host watches monitor topology and foreground-window changes. Fullscreen windows can suspend any window that opts into that policy. Windows opting into focus notifications produce generic `window.focusChanged` events; Chronos uses that event to implement menu auto-hide.

## Reload behavior

Reload parses and resolves a candidate configuration before it changes active
windows. If validation fails, Aether keeps the current configuration. Applying
a theme creates a fresh logical runtime inside the same Aether process, disposes
prior windows and bridge instances, rotates backend scope, and keeps the
inherited control channel and command pipe alive. Configuration changes emit the
new theme activation generation so Chronos can reject stale events.

Control EOF marks Aether unavailable to Chronos and fails pending requests.
Backend EOF closes that backend session; the next page request creates a fresh
connection.

## Native boundary

`UltralightNative` contains the C API bindings. `NativeMethods` and `NativeWindowApi` contain Win32 interop. Keep ownership and thread affinity explicit when you change these layers. Failures often appear as invalid handles, geometry mismatches, or shutdown races.
