#requires -Version 5.1
# Exercise deletion only inside an isolated, regenerable fixture repository.
$ErrorActionPreference = 'Stop'
$fixture = Join-Path (Split-Path -Parent $PSScriptRoot) ('.artifacts/cleanup-tests/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'scripts') -Force | Out-Null
$cleaner = Join-Path $fixture 'scripts/clean-project.ps1'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'clean-project.ps1') -Destination $cleaner

function Add-FixtureFile([string] $Relative) {
    $path = Join-Path $fixture $Relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    Set-Content -LiteralPath $path -Value 'regenerable test fixture'
}

function Assert-Exists([string] $Relative, [bool] $Expected) {
    if ((Test-Path -LiteralPath (Join-Path $fixture $Relative)) -ne $Expected) {
        throw "Unexpected cleanup result: $Relative (expected existence: $Expected)"
    }
}

$removable = @('app/bin/output.dll', 'app/obj/generated.cs', '.artifacts/run.log',
    'scripts/__pycache__/tool.pyc', '.pytest_cache/state', '.ruff_cache/state',
    '.codex-check/bin/output.dll', 'TestResults/result.xml')
$preserved = @('app/source.cs', '.codex/config.toml', '.codex-notes/notes.md',
    'precached/experiment/bin/reference.txt', 'backups/app/bin/reference.txt',
    'artifacts/reference.txt', 'native/bin/library.dll', 'node_modules/tool/bin/tool.js',
    'nested/.git/config', 'nested/bin/output.dll', 'obj/.git/config', 'obj/source.cs')
foreach ($path in ($removable + $preserved)) { Add-FixtureFile $path }

& $cleaner -WhatIf | Out-Null
foreach ($path in ($removable + $preserved)) { Assert-Exists $path $true }
& $cleaner | Out-Null
foreach ($path in $removable) { Assert-Exists $path $false }
foreach ($path in $preserved) { Assert-Exists $path $true }

# Tracked outputs must never be deleted, even if their directory looks disposable.
& git -C $fixture init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Unable to initialize fixture repository.' }
Add-FixtureFile 'tracked/bin/reference.txt'
& git -C $fixture add -- tracked/bin/reference.txt
if ($LASTEXITCODE -ne 0) { throw 'Unable to track fixture file.' }
Add-FixtureFile 'other/obj/output.dll'
$rejected = $false
try { & $cleaner | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Cleaner did not report a protected tracked directory.' }
Assert-Exists 'tracked/bin/reference.txt' $true
Assert-Exists 'other/obj/output.dll' $false

# Keep the recovered-key guard intact; this is dummy data, never a real key.
Add-FixtureFile '.artifacts/update-signing/private.pem'
$rejected = $false
try { & $cleaner | Out-Null } catch { $rejected = $true }
if (-not $rejected) { throw 'Cleaner did not report a recovered signing key.' }
Assert-Exists '.artifacts/update-signing/private.pem' $true
# Remove only our dummy sentinel, so the outer workspace cleaner can reclaim the fixture.
Remove-Item -LiteralPath (Join-Path $fixture '.artifacts/update-signing/private.pem')
Write-Host 'PASS cleanup: preview, build/Python caches, archives, nested repositories, tracked files and recovered-key protection.'
