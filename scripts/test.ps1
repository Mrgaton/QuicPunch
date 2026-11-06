# Build once and run only the requested suites. Successful builds stay silent.
[CmdletBinding()]
param(
    [string[]] $Tests = @(),
    [switch] $Web,
    [switch] $Browser,
    [string] $BrowserFilter,
    [switch] $NoBuild,
    [string] $Configuration = 'Test'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
$showLog = $VerbosePreference -eq 'Continue'
$failed = $false

function Write-CompactDiagnostic([string] $Line) {
    $Line = $Line.Replace($root + [IO.Path]::DirectorySeparatorChar, '')
    $Line = $Line -replace '^.*?: (error\b)', '$1'
    if ($Line.Length -gt 240) { $Line = $Line.Substring(0, 140) + ' ... ' + $Line.Substring($Line.Length - 95) }
    [Console]::Error.WriteLine($Line)
}

if ($Tests.Count -eq 0 -and -not $Web -and -not $Browser) {
    [Console]::Error.WriteLine('Usage: ./scripts/test.ps1 -Tests sha3,antireplay [-Web] [-Browser] [-BrowserFilter Name] [-NoBuild] [-Verbose]')
    exit 2
}

Push-Location $root
try {
    $logDirectory = Join-Path '.artifacts' 'test-output'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $runId = [Guid]::NewGuid().ToString('N')

    if ($Tests.Count -gt 0) {
        $project = Join-Path $root 'QuicPunchTests/QuicPunchTests.csproj'
        if (-not $NoBuild) {
            $buildLog = Join-Path $logDirectory "build-$runId.log"
            & dotnet build $project --configuration $Configuration --nologo --verbosity quiet *> $buildLog
            $buildExit = $LASTEXITCODE
            if ($showLog) { Get-Content -LiteralPath $buildLog }
            if ($buildExit -ne 0) {
                [Console]::Error.WriteLine("FAIL build (exit=$buildExit)")
                if (-not $showLog) {
                    Get-Content -LiteralPath $buildLog | Where-Object { $_ -match '\berror\b' } |
                        Select-Object -Unique -First 3 | ForEach-Object {
                            Write-CompactDiagnostic $_
                        }
                }
                [Console]::Error.WriteLine("Log: $buildLog")
                exit 1
            }
        }

        foreach ($test in $Tests) {
            $flag = if ($test.StartsWith('--')) { $test } else { "--test-$test" }
            $runArgs = @('run', '--project', $project, '--configuration', $Configuration, '--no-build', '--', $flag)
            if ($showLog) { $runArgs += '--verbose' }
            & dotnet @runArgs
            if ($LASTEXITCODE -ne 0) { $failed = $true }
        }
    }

    if ($Web -or $Browser) {
        $previousConfiguration = $env:QUICNET_TEST_CONFIGURATION
        try {
            $env:QUICNET_TEST_CONFIGURATION = $Configuration
            $modes = @()
            if ($Web) { $modes += '--web' }
            if ($Browser) { $modes += '--browser' }
            foreach ($mode in $modes) {
                $nodeArgs = @((Join-Path $root 'scripts/test-web.mjs'), $mode)
                if ($mode -eq '--browser' -and $BrowserFilter) { $nodeArgs += "--filter=$BrowserFilter" }
                if ($NoBuild -or $Tests.Count -gt 0) { $nodeArgs += '--no-build' }
                if ($showLog) { $nodeArgs += '--verbose' }
                & node @nodeArgs
                if ($LASTEXITCODE -ne 0) { $failed = $true }
            }
        } finally {
            $env:QUICNET_TEST_CONFIGURATION = $previousConfiguration
        }
    }

} catch {
    Write-CompactDiagnostic "FAIL runner: $($_.Exception.Message)"
    exit 1
} finally {
    Pop-Location
}

if ($failed) { exit 1 }
exit 0
