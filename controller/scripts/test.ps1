[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Debug',
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$SolutionPath = Join-Path $RepositoryRoot 'Chronos.sln'
$ProjectPath = Join-Path $RepositoryRoot 'Chronos.Tests\Chronos.Tests.csproj'
$BuildArguments = @('build', $SolutionPath, '--configuration', $Configuration, '-m:1')
$RunArguments = @('run', '--project', $ProjectPath, '--configuration', $Configuration, '--no-build', '--no-restore') + $Arguments

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
