#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^http://127\.0\.0\.1:[0-9]+/?$')][string]$BaseUri = 'http://127.0.0.1:5000',
    [ValidateRange(1, 600)][int]$Seconds = 45,
    [ValidateRange(200, 5000)][int]$IntervalMs = 500,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) {
    $repo = Split-Path $PSScriptRoot -Parent
    $OutputDirectory = Join-Path $repo ('.artifacts/quic-performance/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$base = $BaseUri.TrimEnd('/')
$release = Invoke-RestMethod "$base/api/updates" -TimeoutSec 5
$metadata = [ordered]@{
    startedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    version = $release.currentVersion
    build = $release.currentBuild
    seconds = $Seconds
    intervalMs = $IntervalMs
    note = 'Read-only capture. Start the speed test in the application. Contains peer IDs and network endpoints; no chat, tokens or keys.'
}
if ($IsWindows) {
    $metadata.adapters = @(Get-NetAdapter | Where-Object Status -eq 'Up' | Select-Object Name,InterfaceDescription,ifIndex,LinkSpeed)
    $metadata.defaultRoutes = @(Get-NetRoute -AddressFamily IPv4 | Where-Object DestinationPrefix -eq '0.0.0.0/0' | Select-Object InterfaceAlias,InterfaceIndex,NextHop,RouteMetric)
}
$metadata | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutputDirectory 'metadata.json') -Encoding utf8
$writer = [IO.StreamWriter]::new((Join-Path $OutputDirectory 'samples.jsonl'), $false, [Text.UTF8Encoding]::new($false))
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    while ($clock.Elapsed.TotalSeconds -lt $Seconds) {
        try {
            $status = Invoke-RestMethod "$base/api/status" -TimeoutSec 5
            # Several application sessions share one physical connection. Keep labels;
            # do not sum their cumulative counters as separate traffic.
            $sessions = @($status.quicSessions | ForEach-Object {
                [ordered]@{peerId=$_.peerId;peerName=$_.peerName;protocol=$_.protocolName;transport=$_.transportType;telemetry=$_.telemetry}
            })
            $writer.WriteLine((@{utc=[DateTimeOffset]::UtcNow.ToString('o');sessions=$sessions} | ConvertTo-Json -Depth 8 -Compress))
            $writer.Flush()
        } catch {
            $writer.WriteLine((@{utc=[DateTimeOffset]::UtcNow.ToString('o');error=$_.Exception.Message} | ConvertTo-Json -Compress))
        }
        Start-Sleep -Milliseconds $IntervalMs
    }
} finally { $writer.Dispose() }
Write-Output "Saved QUIC diagnostic capture: $OutputDirectory"
