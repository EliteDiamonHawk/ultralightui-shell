# Aether renderer

Aether hosts Ultralight pages as generic Windows surfaces, with wallpaper and widget adapters. The project targets Windows x64, .NET 9, and WinForms. Chronos owns menu policy and creates its menu through Aether's generic window API.

## Quick start

1. Copy [`docs/configuration/example.json`](docs/configuration/example.json).
2. Replace its sample URLs.
3. Install the SDK described in [`docs/development.md`](docs/development.md).
4. Start the host from `renderer/`:

   ```powershell
   .\scripts\run.ps1 -Arguments @('--config', '.\docs\configuration\example.json')
   ```

The host can run standalone with wallpaper, widgets, or generic `windows`. Aether does not accept a `menu` section.

Use [`docs/runtime-control.md`](docs/runtime-control.md) to show, hide, pause, reload, inspect, or stop the running host.

## Documentation

- [`docs/README.md`](docs/README.md): renderer documentation index.
- [`docs/configuration/README.md`](docs/configuration/README.md): configuration files and editor support.
- [`docs/configuration/configuration.md`](docs/configuration/configuration.md): sections, fields, geometry, defaults, and reload rules.
- [`docs/configuration/schema.json`](docs/configuration/schema.json): JSON Schema for canonical config files.
- [`runtime-control.md`](docs/runtime-control.md): startup options, commands, pipe responses, and exit codes.
- [`architecture.md`](docs/architecture.md): process, thread groups, windows, embedding, and suspension.
- [`development.md`](docs/development.md): SDK layout, build, test, publish, and native checks.
- [`troubleshooting.md`](docs/troubleshooting.md): common startup, config, geometry, and reload failures.

## Rules worth knowing

- Pass each config file with its own `--config` option.
- Put each top-level section in one file only.
- Monitor numbers start at `1` and follow `Screen.AllScreens` order.
- Use HTTP, HTTPS, or `file:` URLs. A bare host name becomes an HTTPS URL.
