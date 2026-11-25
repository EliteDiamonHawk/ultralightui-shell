[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'src\Nyx\Nyx.csproj'
$DotnetArguments = @('build', $ProjectPath, '--configuration', $Configuration, '-m:1')
if ($NoRestore) { $DotnetArguments += '--no-restore' }
$DotnetArguments += $Arguments

Push-Location $RepositoryRoot
try { & dotnet @DotnetArguments; $ExitCode = $LASTEXITCODE }
finally { Pop-Location }
exit $ExitCode
