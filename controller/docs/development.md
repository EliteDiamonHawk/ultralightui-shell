# Development

Requirements: Windows 10/11 x64 and .NET 9 SDK.

From `controller/`:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\run.ps1
.\scripts\publish.ps1 -OutputPath .\publish
```

Build/test scripts default to Release/Debug conventions matching renderer.
`run.ps1 -Arguments @('--renderer-path', 'C:\path\Aether.exe')` overrides
renderer discovery.

Unit tests avoid native Aether and hook installation. Windows integration checks
must exercise inherited control startup, `Aether.Commands`, config restoration,
tray suppression, backend grant failure, and Win-key shutdown restoration.
