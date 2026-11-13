[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'UltraLightStartMenu\csharp\UltraLightStartMenu.csproj'
$DotnetArguments = @('run', '--project', $ProjectPath, '--configuration', $Configuration, '--') + $Arguments

Push-Location $RepositoryRoot
try {
    & dotnet @DotnetArguments
    $ExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}
exit $ExitCode
