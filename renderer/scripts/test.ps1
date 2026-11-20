[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [switch] $IncludeUltralight,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SolutionPath = Join-Path $RepositoryRoot 'Aether.sln'
$TestProjectPath = Join-Path $RepositoryRoot 'Aether.Tests\Aether.Tests.csproj'
$BuildArguments = @('build', $SolutionPath, '--configuration', $Configuration, '-m:1')
if (-not $IncludeUltralight) { $BuildArguments += '-p:IncludeUltralight=false' }
$RunArguments = @('run', '--project', $TestProjectPath, '--configuration', $Configuration, '--no-build', '--no-restore') + $Arguments

Push-Location $RepositoryRoot
try {
    & dotnet @BuildArguments
    $ExitCode = $LASTEXITCODE
    if ($ExitCode -eq 0) {
        & dotnet @RunArguments
        $ExitCode = $LASTEXITCODE
    }
}
finally { Pop-Location }
exit $ExitCode
