[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'src\Nyx\Nyx.csproj'
Push-Location $RepositoryRoot
try { & dotnet run --project $ProjectPath --configuration $Configuration -- @Arguments; $ExitCode = $LASTEXITCODE }
finally { Pop-Location }
exit $ExitCode
