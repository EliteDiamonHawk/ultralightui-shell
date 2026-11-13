# Configuration

Pass one or more JSON files with repeated `--config` options. The parser accepts comments and trailing commas. Each file must contain a JSON object.

Use one top-level section per file. The loader combines sections across files; it does not merge two copies of the same section. Valid sections:

- `wallpaper`: an array of wallpaper profiles.
- `widgets`: an array of widget definitions.
- `start-menu`: one start-menu definition.

Add at least one section. The loader rejects unknown sections, unknown fields, duplicate fields, and duplicate sections. Field names do not depend on case.

See [`README.md`](README.md) for the files in this directory and [`schema.json`](schema.json) for editor validation.

## URLs

Add `url` to each wallpaper, widget, and start-menu definition. The loader accepts `http`, `https`, and `file` schemes. It adds `https://` to a value without a scheme.

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
| `window-ids` | No | Explicit logical IDs. When present, one window is created per ID over the virtual screen; this takes precedence over `mode`. |
| `transparent` | No | Transparent Ultralight window. Default `false`. |
| `thread-group` | No | Runtime group. Default `wallpaper`. |

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

Add `x`, `y`, one position-scale field, one size-scale field, `anchor`, one width field, one height field, `monitor`, and `url`.

Use `width` and `height` in new files. The parser also accepts `w` and `h`. It accepts `pos-scale` and `size-scale` as aliases for `pos-scaled` and `size-scaled`.

Set `anchor` to `top-left`, `top`, `top-right`, `left`, `center`, `right`, `bottom-left`, `bottom`, or `bottom-right`. The parser ignores spaces and hyphens.

- Set `pos-scaled: true` to express `x` and `y` as percentages of the monitor origin. Keep both values between `-100` and `100`.
- Set `pos-scaled: false` to express `x` and `y` as pixel offsets from the selected anchor.
- Set `size-scaled: true` to express dimensions as percentages of the smaller monitor dimension. Keep each dimension at or below `100`.
- Set `size-scaled: false` to express dimensions in pixels.
- Widgets use `transparent: true` and `thread-group: widgets` when you omit those fields.

## Start menu

Give `start-menu` the same geometry and URL fields as a widget. Add these fields when needed:

| Field | Required | Default | Meaning |
| --- | --- | --- | --- |
| `auto-hide` | No | `true` | Hide when another, non-owned foreground window becomes active. |
| `hidden-behavior` | No | `none` | `none`, `pause`/`suspend`, or `unload`. |
| `transparent` | No | `true` | Transparent Ultralight window. |
| `thread-group` | No | `start-menu` | Runtime group. |

`none` hides the loaded window. `pause` hides the window and suspends its view. `unload` destroys the window; `--show` creates it again.

## Thread groups and reload

Windows with equal `thread-group` values share one Ultralight runtime and one STA thread. Different groups run in separate groups. The loader requires unique logical IDs across resolved windows.

`--reload <group>` reparses all config files and reloads one existing group. If the edit changes group membership, use `--reload-all`. That command creates new groups and disposes removed groups.
