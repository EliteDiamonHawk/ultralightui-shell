# Renderer configuration

Pass one or more JSON files with repeated `--config` options. The parser accepts
comments and trailing commas. Each file must contain a JSON object.

Use one top-level section per file. The loader combines sections across files; it does not merge two copies of the same section. Valid sections:

- `windows`: an array of generic window definitions.
- `wallpaper`: an array of wallpaper profiles. This is a lightweight desktop-window adapter.
- `widgets`: an array of widget definitions. This is a lightweight desktop-window adapter.
- `image-widgets`: an array of native PNG/JPEG/BMP/GIF widgets. These bypass Ultralight.
- `variables`: an object of JSON values shared with bridge-enabled pages.
- `cues`: an array of Chronos cue names accepted by bridge-enabled pages.

Chronos-managed themes may contain Chronos-only `before` and `after` sections.
Chronos removes those sections and sends Aether one sanitized inline document;
direct Aether configuration does not accept them.

Add at least one section. Aether does not accept a `menu` section; Chronos owns
menu policy and creates the menu through the generic window API. The loader
rejects unknown sections, unknown fields, duplicate fields, and duplicate
sections. Field names do not depend on case.

See [`README.md`](README.md) for the files in this directory and [`schema.json`](schema.json) for editor validation.

## URLs

Add `url` to each generic window, wallpaper, or widget definition. The loader
accepts `http`, `https`, and `file` schemes. It adds `https://` to a value
without a scheme.

```json
"url": "https://example.com/ui"
```

```json
"url": "file:///C:/Wallpapers/home/index.html"
```

The parser also accepts numeric and boolean strings. New files should use JSON numbers and booleans.

## Wallpaper profiles

```json
{
  "url": "https://example.com/wallpaper",
  "mode": "all",
  "monitor-urls": {
    "1": "https://example.com/primary",
    "2": "https://example.com/secondary"
  },
  "window-ids": ["custom-wallpaper"],
  "transparent": false,
  "thread-group": "desktop"
}
```

Fields:

| Field | Required | Meaning |
| --- | --- | --- |
| `url` | Yes | Base page URL. |
| `mode` | No | `span` (default), `all`, or `single`. |
| `monitor` | For `single` | One-based monitor number. Defaults to `1` when omitted. |
| `monitor-urls` | No | Object keyed by one-based monitor number; overrides `url` for selected monitors. |
| `window-ids` | No | Explicit logical IDs. Aether creates one window per ID over the virtual screen; this takes precedence over `mode`. |
| `transparent` | No | Transparent Ultralight window. Default `false`. |
| `thread-group` | No | Runtime group. Default `wallpaper`. |
| `bridge` | No | Page backend and control-route permissions. |

Set `id` when another process or page needs a stable window identifier. Aether
generates an ID from the profile index and monitor number when you omit it. A
resolved ID may contain letters, numbers, `.`, `_`, `:`, and `-`, and may contain
up to 128 characters.

`span` uses one virtual-screen window. `all` uses one window per monitor. `single` uses the selected `monitor`.

## Widgets

```json
{
  "x": 50,
  "y": 50,
  "pos-scaled": true,
  "size-scaled": false,
  "anchor": "center",
  "width": 480,
  "height": 240,
  "monitor": 1,
  "url": "https://example.com/widget",
  "transparent": true,
  "thread-group": "desktop"
}
```

Add `x`, `y`, one position-scale field, one size-scale field, `anchor`, one width
field, one height field, `monitor`, and `url`.

Use `width` and `height` in new files. The parser also accepts `w` and `h`. It accepts `pos-scale` and `size-scale` as aliases for `pos-scaled` and `size-scaled`.

Set `anchor` to `top-left`, `top`, `top-right`, `left`, `center`, `right`,
`bottom-left`, `bottom`, or `bottom-right`. The parser ignores spaces and
hyphens.

- Set `pos-scaled: true` to express `x` and `y` as percentages of the monitor origin. Keep both values between `-100` and `100`.
- Set `pos-scaled: false` to express `x` and `y` as pixel offsets from the selected anchor.
- Set `size-scaled: true` to express dimensions as percentages of the smaller monitor dimension. Keep each dimension at or below `100`.
- Set `size-scaled: false` to express dimensions in pixels.
- Widgets use `transparent: true` and `thread-group: widgets` when you omit those fields.

Set `id` for stable window control. Add `bridge` when the page needs backend or
control access.

## Native image widgets

Use `image-widgets` for low-overhead raster content. Static images do not start
an animation timer; GIFs use frame delays and pause when hidden by default. The
native backend accepts PNG, JPEG, BMP, and GIF files, and does not expose a JS
bridge.

```json
{
  "image-widgets": [{
    "id": "character",
    "source": "assets/character.gif",
    "type": "auto",
    "pause-when-hidden": true,
    "playback-speed": 1.0,
    "fit": "contain",
    "opacity": 1.0,
    "rotation": 0,
    "thread-group": "image-widgets",
    "placement": {
      "mode": "floating",
      "monitor": 1,
      "anchor": "bottom-right",
      "offset-x": -20,
      "offset-y": -20,
      "width": 240,
      "height": 240,
      "always-on-top": false,
      "click-through": true
    }
  }]
}
```

`source` is resolved relative to the configuration file. Absolute local paths
and `file:` URLs are also accepted; HTTP and HTTPS sources are rejected. Width,
height, and offsets are DIPs and are converted using the selected monitor's
DPI. `mode: "floating"` uses Aether's normal top-level window path, while
`mode: "desktop"` uses the existing WorkerW desktop embedding path. Click
through is whole-window only; alpha-aware hit testing is not enabled.

The GIF compositor honors frame rectangles, transparency, frame delays, loop
counts, and disposal methods 0/1/2/3. It updates the DirectComposition surface
using the incoming/disposal dirty region and falls back to a full update when
the changed area is large.

## Generic windows

Use `windows` for any top-level or desktop window that does not need one of the
small wallpaper/widget adapters:

```json
{
  "windows": [{
    "id": "clock",
    "url": "file:///C:/Shell/clock/index.html",
    "runtime-group": "utility",
    "geometry": {
      "coordinate-space": "monitor",
      "monitor": 1,
      "anchor": "top-right",
      "position": { "x": 24, "y": 24, "unit": "pixels" },
      "size": { "width": 320, "height": 100, "unit": "pixels" }
    },
    "surface": {
      "kind": "top-level",
      "z-order": "topmost-when-focused"
    },
    "lifecycle": {
      "initial": {
        "visibility": "shown",
        "runtime": "active"
      },
      "pause-on-fullscreen": true
    },
    "input": { "mouse": "native", "keyboard": "native", "focus": "on-show" },
    "notifications": { "focus": true }
  }]
}
```

Generic definitions require `id`, `url`, `geometry.position`, and
`geometry.size`. Geometry uses one unit for position and one unit for size;
each unit is `pixels` or `percent`. The coordinate space is `monitor`,
`work-area`, or `virtual-screen`, and the anchor is one of the nine standard
positions. `surface.kind` is `desktop` or `top-level`. Surface settings contain
z-order, transparency, framing, resizing, taskbar visibility, and optional
work-area reservation. Lifecycle settings contain the initial visibility/runtime
state and fullscreen pause policy. Input, notifications, and bridge access
remain opt-in capability blocks.

The initial runtime may be `active`, `paused`, or `unloaded`. An unloaded
definition is registered and queryable but has no native or Ultralight instance;
`reload` creates it. `runtime: "unloaded"` requires `visibility: "hidden"`.

Generic window fields use exact canonical names and JSON types. Flat fields such
as `x`, `width`, `placement`, and `initial-visible` are not accepted. Generic
window URLs must be absolute `http`, `https`, or `file` URLs.

The inherited control pipe exposes `GET|POST|PUT|DELETE /v1/renderer/windows`
and `POST /v1/renderer/windows/{id}/actions`. Actions are `show`, `hide`,
`toggle`, `focus`, `blur`, `pause`, `resume`, `unload`, and `reload`.
Unloading retains the window definition; reloading recreates it. Aether emits generic lifecycle, change, focus, and
visibility events. Chronos uses these same operations for its menu; Aether has
no menu-specific route, command, or runtime model.

## Bridge policy

Add `bridge` to a generic window, wallpaper, or widget when the page needs
backend access:

```json
"bridge": {
  "enabled": true,
  "backend-ids": ["nyx"],
  "control-routes": ["/v1/status"],
  "runtime-commands": ["showWindow", "hideWindow", "unloadWindow", "reloadWindow"]
}
```

`backend-ids` limits the backends that the page can request. `control-routes`
limits routes exposed through the renderer bridge. Each route must begin with
`/v1/`. `runtime-commands` grants page JavaScript access to these safe generic
Aether commands: `showWindow`, `hideWindow`, `unloadWindow`, `reloadWindow`,
`pause`, `resume`, `status`, and group `reload`. Permissions default to empty.

When granted, page JavaScript can call:

```js
await window.aether.command({ command: "hideWindow", targetId: "widget-1" });
await window.aether.command({ command: "pause", group: "desktop" });
```

Window commands require `targetId`. Group commands accept optional `group` or
`all`, but not both. The Promise resolves to `{ command, message }`; invalid,
unauthorized, or failed commands reject. Chronos starts a configured backend on
the first request, grants Aether short-lived access, and revokes that grant when
the session ends.

## Variables and events

Top-level variables accept any JSON value:

```json
"variables": {
  "theme": "dark",
  "refresh-ms": 1000
}
```

Bridge-enabled pages query them with `window.aether.variables.get("theme")`.
Unknown keys return `undefined`.

Declare Chronos cues with names:

```json
"cues": ["menu.toggle", "next"]
```

Streams and cues share one event bus:

```js
const unsubscribe = window.aether.onEvent(event => {
  if (event.source === "stream") console.log(event.streamId, event.data);
  if (event.source === "cue") console.log(event.name, event.data);
});
```

Cue-only subscriptions use `window.aether.onCue(callback)` and receive
`{ name, data }`. Existing `window.renderer.onStream(callback)` remains
available for stream payloads. Chronos sends cue payloads; future keybind
producers can use them.

## Runtime groups and reload

Generic windows with equal `runtime-group` values share one Ultralight runtime and one STA thread. Different groups run in separate groups. The loader requires unique logical IDs across resolved windows.

`--reload <group>` reparses all config files and reloads one existing group. If the edit changes group membership, use `--reload-all`. That command creates new groups and disposes removed groups.
