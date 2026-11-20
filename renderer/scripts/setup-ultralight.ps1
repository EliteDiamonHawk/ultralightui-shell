[CmdletBinding()]
param(
    # Optional path to an SDK archive downloaded manually.
    [string] $SdkArchivePath
)

$ErrorActionPreference = 'Stop'

$UltralightSdkVersion = '2.0.0-beta.2'
$UltralightSdkDownloadUrl = "https://ultralig.ht/api/v1/sdk/download?platform=windows-x64&version=$UltralightSdkVersion"

# Script lives in renderer/scripts; install SDK beside Aether under renderer/third_party.
$RendererRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$ThirdPartyRoot = Join-Path $RendererRoot 'third_party'
$InstallRoot = Join-Path $ThirdPartyRoot 'ultralight'
$ManifestPath = Join-Path $InstallRoot 'sdk-version.json'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("khaosshell-ultralight-" + [guid]::NewGuid().ToString('N'))
$StagingRoot = Join-Path $ThirdPartyRoot (".ultralight-staging-" + [guid]::NewGuid().ToString('N'))
$BackupRoot = Join-Path $ThirdPartyRoot (".ultralight-backup-" + [guid]::NewGuid().ToString('N'))
$BackupCreated = $false

function Test-RequiredPath {
    param([string] $Root, [string] $RelativePath)
    Test-Path -LiteralPath (Join-Path $Root $RelativePath) -PathType Leaf
}

function Assert-SdkLayout {
    param([string] $Root)

    $requiredFiles = @(
        'include\AppCore\CAPI.h',
        'include\Ultralight\CAPI\CAPI_JSAPI.h',
        'include\Ultralight\CAPI\CAPI_JSValue.h',
        'include\Ultralight\CAPI\CAPI_DOMData.h',
        'bin\AppCore.dll',
        'bin\Ultralight.dll',
        'bin\UltralightCore.dll',
        'bin\WebCore.dll'
    )
    $missing = @($requiredFiles | Where-Object { -not (Test-RequiredPath $Root $_) })
    $resourcesPath = Join-Path $Root 'resources'

    if (-not (Test-Path -LiteralPath $resourcesPath -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $resourcesPath -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1)) {
        $missing += 'resources\ (containing SDK resource files)'
    }

    if ($missing.Count -gt 0) {
        throw "Ultralight SDK validation failed. Missing: $($missing -join ', ')"
    }
}

function Find-SdkRoot {
    param([string] $ExtractRoot)

    $candidates = @(
        Get-Item -LiteralPath $ExtractRoot
        Get-ChildItem -LiteralPath $ExtractRoot -Directory -Recurse -ErrorAction SilentlyContinue
    )
    foreach ($candidate in $candidates) {
        if ((Test-Path (Join-Path $candidate.FullName 'include')) -and
            (Test-Path (Join-Path $candidate.FullName 'bin')) -and
            (Test-Path (Join-Path $candidate.FullName 'resources'))) {
            return $candidate.FullName
        }
    }
    throw 'Could not find SDK root containing include, bin, and resources directories in archive.'
}

function Test-InstalledSdk {
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { return $false }
    try {
        $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
        if ($manifest.version -ne $UltralightSdkVersion) { return $false }
        Assert-SdkLayout $InstallRoot
        return $true
    } catch {
        return $false
    }
}

function Expand-SdkArchive {
    param([string] $Archive, [string] $Destination)

    if ([IO.Path]::GetExtension($Archive).ToLowerInvariant() -eq '.zip') {
        Expand-Archive -LiteralPath $Archive -DestinationPath $Destination -Force
        return
    }

    $extractor = @('7z', '7za', '7zz') | ForEach-Object {
        Get-Command $_ -ErrorAction SilentlyContinue
    } | Select-Object -First 1
    if (-not $extractor) {
        throw "SDK archive '$Archive' is not a ZIP. Install 7-Zip or provide a ZIP archive."
    }
    & $extractor.Source x $Archive "-o$Destination" -y | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Archive extraction failed with exit code $LASTEXITCODE." }
}

try {
    New-Item -ItemType Directory -Path $ThirdPartyRoot -Force | Out-Null

    if (Test-InstalledSdk) {
        Write-Host "Ultralight SDK $UltralightSdkVersion already installed at $InstallRoot."
        exit 0
    }

    New-Item -ItemType Directory -Path $TempRoot, $StagingRoot -Force | Out-Null

    if ($SdkArchivePath) {
        $archive = (Resolve-Path -LiteralPath $SdkArchivePath -ErrorAction Stop).Path
        if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
            throw "SDK archive path is not a file: $SdkArchivePath"
        }
        Write-Host "Using local Ultralight SDK archive: $archive"
    } else {
        $archive = Join-Path $TempRoot 'ultralight-sdk.7z'
        Write-Host "Downloading Ultralight SDK $UltralightSdkVersion..."
        try {
            Invoke-WebRequest -Uri $UltralightSdkDownloadUrl -OutFile $archive -UseBasicParsing
        } catch {
            throw "Automatic SDK download failed from '$UltralightSdkDownloadUrl'. Rerun with -SdkArchivePath <archive>. Cause: $($_.Exception.Message)"
        }
    }

    $extractRoot = Join-Path $TempRoot 'extracted'
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Expand-SdkArchive $archive $extractRoot
    $sdkRoot = Find-SdkRoot $extractRoot

    Get-ChildItem -LiteralPath $sdkRoot -Force | Copy-Item -Destination $StagingRoot -Recurse -Force
    [ordered]@{
        version = $UltralightSdkVersion
        platform = 'win-x64'
        source = if ($SdkArchivePath) { 'local-archive' } else { $UltralightSdkDownloadUrl }
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StagingRoot 'sdk-version.json') -Encoding UTF8
    Assert-SdkLayout $StagingRoot

    if (Test-Path -LiteralPath $InstallRoot) {
        Rename-Item -LiteralPath $InstallRoot -NewName (Split-Path $BackupRoot -Leaf)
        $BackupCreated = $true
    }
    Rename-Item -LiteralPath $StagingRoot -NewName (Split-Path $InstallRoot -Leaf)
    Assert-SdkLayout $InstallRoot

    if ($BackupCreated) {
        Remove-Item -LiteralPath $BackupRoot -Recurse -Force
        $BackupCreated = $false
    }
    Write-Host "Installed Ultralight SDK $UltralightSdkVersion at $InstallRoot."
} catch {
    if ($BackupCreated -and -not (Test-Path -LiteralPath $InstallRoot)) {
        Rename-Item -LiteralPath $BackupRoot -NewName (Split-Path $InstallRoot -Leaf) -ErrorAction SilentlyContinue
    }
    Write-Error $_.Exception.Message
    exit 1
} finally {
    if (Test-Path -LiteralPath $TempRoot) { Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $StagingRoot) { Remove-Item -LiteralPath $StagingRoot -Recurse -Force -ErrorAction SilentlyContinue }
    if ((Test-Path -LiteralPath $BackupRoot -PathType Container) -and -not $BackupCreated) { Remove-Item -LiteralPath $BackupRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
