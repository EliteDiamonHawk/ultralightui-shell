[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoRestore,
    [string[]] $Arguments = @()
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ProjectPath = Join-Path $RepositoryRoot 'src\Chronos\Chronos.csproj'
$LocalAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($LocalAppData)) {
    throw 'Could not resolve the local application-data directory.'
}

$InstallationParent = Join-Path $LocalAppData 'KhaosShell\Chronos'
$InstallationPath = Join-Path $InstallationParent 'installation'
$StagingPath = Join-Path ([IO.Path]::GetTempPath()) "KhaosShell-Chronos-install-$([Guid]::NewGuid().ToString('N'))"

try {
    $DotnetArguments = @('publish', $ProjectPath, '--configuration', $Configuration, '--output', $StagingPath)
    if ($NoRestore) { $DotnetArguments += '--no-restore' }
    $DotnetArguments += $Arguments
    Push-Location $RepositoryRoot
    try {
        & dotnet @DotnetArguments
        $PublishExitCode = $LASTEXITCODE
    }
    finally { Pop-Location }

    if ($PublishExitCode -ne 0) {
        exit $PublishExitCode
    }

    if (-not (Test-Path -LiteralPath $StagingPath -PathType Container)) {
        throw "Publish output was not created: $StagingPath"
    }

    New-Item -ItemType Directory -Path $InstallationParent -Force | Out-Null
    if (Test-Path -LiteralPath $InstallationPath) {
        Remove-Item -LiteralPath $InstallationPath -Recurse -Force
    }
    Move-Item -LiteralPath $StagingPath -Destination $InstallationPath
    Write-Output "Installed Chronos to $InstallationPath"
}
finally {
    if (Test-Path -LiteralPath $StagingPath) {
        Remove-Item -LiteralPath $StagingPath -Recurse -Force
    }
}
