# Configuration reference

Keep renderer configuration files in this directory when you want a local, self-contained example set.

Files:

- [`configuration.md`](configuration.md) explains sections, fields, defaults, geometry, and reload behavior.
- [`schema.json`](schema.json) describes the canonical JSON shape for editor validation.
- [`example.json`](example.json) shows wallpaper, widget, and start-menu sections together.

Start the renderer from `renderer/`:

```powershell
.\scripts\run.ps1 -Arguments @('--config', '.\docs\configuration\example.json')
```

You can split sections across files. Pass each file with another `--config` option. Put each top-level section in one file only.

The parser accepts comments, trailing commas, case-insensitive field names, bare host names, and numeric or boolean strings for compatibility. Use the canonical field names and JSON types shown in `schema.json` for new files.
