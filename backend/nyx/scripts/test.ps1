[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'tests\Nyx.Tests\Nyx.Tests.csproj'
$BuildArguments = @('build', $ProjectPath, '--configuration', $Configuration, '-m:1')
& dotnet @BuildArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Push-Location $RepositoryRoot
try { & dotnet run --project $ProjectPath --configuration $Configuration --no-build --no-restore -- @Arguments; $ExitCode = $LASTEXITCODE }
finally { Pop-Location }
exit $ExitCode
