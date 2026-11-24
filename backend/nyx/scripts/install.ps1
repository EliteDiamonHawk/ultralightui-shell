[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($LocalAppData)) { throw 'Could not resolve the local application-data directory.' }
$InstallationParent = Join-Path $LocalAppData 'KhaosShell\Nyx'
$InstallationPath = Join-Path $InstallationParent 'installation'
$StagingPath = Join-Path ([IO.Path]::GetTempPath()) "KhaosShell-Nyx-install-$([Guid]::NewGuid().ToString('N'))"

try {
    & (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration -OutputPath $StagingPath -NoRestore:$NoRestore -Arguments $Arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    New-Item -ItemType Directory -Path $InstallationParent -Force | Out-Null
    if (Test-Path -LiteralPath $InstallationPath) { Remove-Item -LiteralPath $InstallationPath -Recurse -Force }
    Move-Item -LiteralPath $StagingPath -Destination $InstallationPath
    Write-Output "Installed Nyx to $InstallationPath"
}
finally { if (Test-Path -LiteralPath $StagingPath) { Remove-Item -LiteralPath $StagingPath -Recurse -Force } }
