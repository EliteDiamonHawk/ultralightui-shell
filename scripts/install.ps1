[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ComponentScripts = @(
    (Join-Path $RepositoryRoot 'renderer\scripts\install.ps1'),
    (Join-Path $RepositoryRoot 'controller\scripts\install.ps1')
)
$ExitCode = 0

foreach ($ScriptPath in $ComponentScripts) {
    try {
        & $ScriptPath @PSBoundParameters
        if ($LASTEXITCODE -ne 0) {
            $ExitCode = $LASTEXITCODE
        }
    }
    catch {
        Write-Error -ErrorAction Continue $_
        if ($ExitCode -eq 0) {
            $ExitCode = 1
        }
    }
}

exit $ExitCode
