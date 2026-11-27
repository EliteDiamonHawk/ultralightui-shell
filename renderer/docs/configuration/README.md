# Configuration files

This directory contains the canonical renderer example, schema, and
configuration reference.

Files:

- [`configuration.md`](configuration.md) documents sections, fields, defaults, geometry, bridge policy, and reload behavior.
- [`schema.json`](schema.json) describes the canonical JSON shape for editor validation.
- [`example.json`](example.json) shows wallpaper, native image-widget, widget, and generic window sections together. Chronos menu definitions are controller-owned.

Start the renderer from `renderer/`:

```powershell
.\scripts\run.ps1 -Arguments @('--config', '.\docs\configuration\example.json')
```

Split sections across files by passing each file with another `--config` option.
Put each top-level section in one file only.

The legacy wallpaper and widget adapters retain their compatibility parsing.
Generic `windows` definitions use exact canonical field names, require nested
geometry/surface/lifecycle blocks, require absolute URLs, and reject string
coercions and the former flat-field aliases. Use the canonical field names and
JSON types from `schema.json` in new files. Chronos-managed themes can also use
[`controller/docs/theme-schema.json`](../../../controller/docs/theme-schema.json)
for menu and hook sections.
