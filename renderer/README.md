# UltraLightRenderer docs

UltraLightRenderer hosts Ultralight pages as Windows wallpapers, widgets, and start menus. The project targets Windows x64, .NET 9, and WinForms.

## Run it

1. Copy [`configuration/example.json`](configuration/example.json).
2. Replace its sample URLs.
3. Install the SDK described in [`development.md`](development.md).
4. Start the host from `renderer/`:

   ```powershell
   .\scripts\run.ps1 -Arguments @('--config', '.\docs\configuration\example.json')
   ```

Use [`runtime-control.md`](runtime-control.md) to show, hide, pause, reload, inspect, or stop the running host.

## Docs/

- [`configuration/README.md`](docs/configuration/README.md): configuration files and editor support.
- [`configuration/configuration.md`](docs/configuration/configuration.md): sections, fields, geometry, defaults, and reload rules.
- [`configuration/schema.json`](docs/configuration/schema.json): JSON Schema for canonical config files.
- [`runtime-control.md`](docs/runtime-control.md): startup options, commands, pipe responses, and exit codes.
- [`architecture.md`](docs/architecture.md): process, thread groups, windows, embedding, and suspension.
- [`development.md`](docs/development.md): SDK layout, build, test, publish, and native checks.
- [`troubleshooting.md`](docs/troubleshooting.md): common startup, config, geometry, and reload failures.

## Rules worth knowing

- Pass each config file with its own `--config` option.
- Put each top-level section in one file only.
- Monitor numbers start at `1` and follow `Screen.AllScreens` order.
- Use HTTP, HTTPS, or `file:` URLs. Bare host names become HTTPS URLs.
