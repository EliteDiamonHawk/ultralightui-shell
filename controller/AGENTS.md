# Controller contributor guide

## Scope

This directory contains Chronos, KhaosShell's controller for the Aether
renderer. Keep controller changes inside `controller/` unless explicitly
requested otherwise.

## Project shape

- `Chronos/`: production WinForms host, lifecycle, IPC, persistence, tray, and
  Win-key policy.
- `Chronos.Tests/`: lightweight executable test harness.
- `scripts/`: build, run, publish, and test wrappers.
- `docs/`: controller architecture, operations, persistence, and IPC.

## Runtime model

Chronos owns one controller instance and launches one Aether process with two
inherited anonymous pipe handles. Aether runtime commands continue to use the
same-user `Aether.Commands` named pipe. Chronos uses the inherited channel for
control requests, renderer logs, and backend grants.

Chronos owns tray state, persistent wrapper settings, backend process grants,
and global Win-key policy. Aether owns renderer windows, Ultralight threads,
and renderer configuration validation.

## Change rules

- Preserve x64, `net9.0-windows`, WinForms, and Windows handle assumptions.
- Never log or persist inherited handles, private pipe names, secrets, or tokens.
- Keep control framing UTF-8 NDJSON, max 1 MiB, versioned `/v1/` paths.
- Do not add a public Chronos command pipe without an explicit contract change.
- Keep Win-key gesture logic testable without installing a native hook.
- Do not commit `bin/`, `obj/`, `publish/`, local backend manifests, or private
  config paths.

## Verification

From `controller/`:

```powershell
.\scripts\test.ps1
.\scripts\build.ps1
```

Native checks require Windows. Test bare-Win behavior only in a disposable
session where normal input restoration can be verified.
