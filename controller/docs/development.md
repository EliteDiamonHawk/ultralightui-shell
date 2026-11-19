# Development

Requirements: Windows 10/11 x64 and the .NET 9 SDK.

From `controller/`:

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
.\scripts\run.ps1
.\scripts\publish.ps1 -OutputPath .\publish
```

Build and publish use Release. Run and test use Debug, matching the renderer
scripts. `run.ps1 -Arguments @('--renderer-path', 'C:\path\Aether.exe')`
overrides renderer discovery.

Unit tests avoid native Aether and hook installation. Windows integration checks
should cover inherited control startup, `Aether.Commands`, theme restoration,
tray suppression, backend grant failure, and Windows Key Override shutdown
restoration.
