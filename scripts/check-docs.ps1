[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Errors = @()

$MarkdownFiles = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -File -Filter '*.md' |
    Where-Object { $_.FullName -notmatch '\\(?:\.git|bin|obj|node_modules|dist)\\' }
$LinkPattern = '\[[^\]]+\]\((?<target><[^>]+>|[^)\s]+)(?:\s+["''][^)]*["''])?\)'

foreach ($MarkdownFile in $MarkdownFiles) {
    $Content = Get-Content -LiteralPath $MarkdownFile.FullName -Raw
    if ($null -eq $Content) { continue }
    foreach ($Match in [regex]::Matches($Content, $LinkPattern)) {
        $Target = $Match.Groups['target'].Value.Trim()
        if ($Target.StartsWith('<') -and $Target.EndsWith('>')) {
            $Target = $Target.Substring(1, $Target.Length - 2)
        }

        if ([string]::IsNullOrWhiteSpace($Target) -or
            $Target.StartsWith('#') -or
            $Target -match '^(?:[a-z][a-z0-9+.-]*:|//)') {
            continue
        }

        $LocalTarget = ($Target -split '[#?]', 2)[0]
        if ([string]::IsNullOrWhiteSpace($LocalTarget)) { continue }
        $LocalTarget = [Uri]::UnescapeDataString($LocalTarget)
        $ResolvedTarget = [IO.Path]::GetFullPath((Join-Path $MarkdownFile.DirectoryName ($LocalTarget -replace '/', '\')))
        if (-not (Test-Path -LiteralPath $ResolvedTarget)) {
            $Errors += "Missing Markdown target '$Target' in $($MarkdownFile.FullName)."
        }
    }
}

$RequiredPaths = @(
    'renderer\tests\Aether.Tests\Aether.Tests.csproj',
    'renderer\tests\Aether.Tests\Program.cs',
    'renderer\scripts\test.ps1',
    'controller\scripts\test.ps1',
    'backend\minimal\src\MinimalBackend\MinimalBackend.csproj',
    'backend\minimal\tests\MinimalBackend.Tests\MinimalBackend.Tests.csproj',
    'contracts\src\RendererContracts\BackendContract.cs',
    'themes.dev\widget-collections\docs\ARCHITECTURE.md'
)

foreach ($RelativePath in $RequiredPaths) {
    $Path = Join-Path $RepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $Path)) {
        $Errors += "Documented command path is missing: $RelativePath."
    }
}

$Python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $Python) {
    Write-Warning 'Schema fixture validation skipped: python is unavailable.'
}
else {
    & $Python.Source -c 'import jsonschema' 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning 'Schema fixture validation skipped: Python package jsonschema is unavailable.'
    }
    else {
        $ValidatorCode = @'
import json
import pathlib
import sys
from jsonschema import Draft202012Validator, RefResolver

schema_path = pathlib.Path(sys.argv[1]).resolve()
fixture_root = pathlib.Path(sys.argv[2]).resolve()
with schema_path.open(encoding="utf-8") as stream:
    schema = json.load(stream)

resolver = RefResolver(base_uri=schema_path.as_uri(), referrer=schema)
validator = Draft202012Validator(schema, resolver=resolver)
cases = {
    "menu-canonical.json": True,
    "menu-aliases.json": True,
    "menu-mixed-width-h.json": True,
    "menu-mixed-w-height.json": True,
    "menu-simultaneous.json": True,
    "before-exit-after.json": True,
    "menu-missing-width.json": False,
    "menu-missing-height.json": False,
    "after-exit-after.json": False,
}

failures = []
for name, expected in cases.items():
    with (fixture_root / name).open(encoding="utf-8") as stream:
        instance = json.load(stream)
    valid = not any(validator.iter_errors(instance))
    if valid != expected:
        failures.append(f"{name}: expected valid={expected}, got valid={valid}")
    else:
        print(f"PASS schema fixture {name}")

if failures:
    for failure in failures:
        print(f"FAIL schema fixture {failure}", file=sys.stderr)
    raise SystemExit(1)
'@
        $SchemaPath = Join-Path $RepositoryRoot 'controller\docs\theme-schema.json'
        $FixtureRoot = Join-Path $RepositoryRoot 'controller\tests\Chronos.Tests\Fixtures'
        & $Python.Source -c $ValidatorCode $SchemaPath $FixtureRoot
        if ($LASTEXITCODE -ne 0) {
            $Errors += 'Schema fixture validation failed.'
        }
    }
}

if ($Errors.Count -gt 0) {
    foreach ($ErrorMessage in $Errors) { Write-Error $ErrorMessage }
    exit 1
}

Write-Output "PASS documentation links and command paths ($($MarkdownFiles.Count) Markdown files checked)."
