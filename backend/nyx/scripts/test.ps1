[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'tests\Nyx.Tests.csproj'
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $RepositoryRoot
try { & dotnet run --project $ProjectPath --configuration $Configuration --no-build --no-restore -- @Arguments; $ExitCode = $LASTEXITCODE }
finally { Pop-Location }
exit $ExitCode
