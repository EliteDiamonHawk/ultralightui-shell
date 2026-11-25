# Configuration files

This directory contains the canonical renderer example and schema.

Files:

- [`configuration.md`](configuration.md) documents sections, fields, defaults, geometry, bridge policy, and reload behavior.
- [`schema.json`](schema.json) describes the canonical JSON shape for editor validation.
- [`example.json`](example.json) shows wallpaper, widget, and generic window sections together. Chronos menu definitions are controller-owned.

Start the renderer from `renderer/`:

```powershell
.\scripts\run.ps1 -Arguments @('--config', '.\docs\configuration\example.json')
```

You can split sections across files. Pass each file with another `--config` option. Put each top-level section in one file only.

The parser accepts comments, trailing commas, case-insensitive field names, bare host names, and numeric or boolean strings for compatibility. Use the canonical field names and JSON types from `schema.json` in new files.
