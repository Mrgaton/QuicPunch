param([string]$Dotnet = '')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $Dotnet) {
    $localSdk = Join-Path $env:LOCALAPPDATA 'NetLocate/BuildTools/dotnet10/dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localSdk) { [IO.Path]::GetFullPath($localSdk) } else { (Get-Command dotnet).Source }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
Push-Location $root
try {
    & $Dotnet publish NetLocate.csproj -c Release -r win-x64 -p:PublishAot=true -o bin/updates/win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed.' }
    $exe = Join-Path $root 'bin/updates/win-x64/NetLocate.exe'
    $bytes = [IO.File]::ReadAllBytes($exe)
    $pe = [BitConverter]::ToInt32($bytes, 0x3c)
    $signature = 'PE' + [char]0 + [char]0
    if ([Text.Encoding]::ASCII.GetString($bytes, $pe, 4) -ne $signature -or
        [BitConverter]::ToUInt16($bytes, $pe + 4) -ne 0x8664 -or
        [BitConverter]::ToUInt16($bytes, $pe + 24) -ne 0x20b) { throw 'Not a Windows x64 PE32+ image.' }
    $clr = $pe + 24 + 112 + 14 * 8
    if ([BitConverter]::ToUInt32($bytes, $clr) -ne 0 -or
        [BitConverter]::ToUInt32($bytes, $clr + 4) -ne 0) { throw 'Unexpected CLR header.' }
    & $exe self-test
    if ($LASTEXITCODE -ne 0) { throw 'Native executable self-tests failed.' }
    $destination = Join-Path $root 'dist/win-x64'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $exe -Destination (Join-Path $destination 'NetLocate.exe') -Force
    [ordered]@{
        framework = 'net10.0'
        runtime = '10.0.13'
        sdk = (& $Dotnet --version)
        rid = 'win-x64'
        publishAot = $true
        clrHeaderPresent = $false
        nativeSelfTests = 'passed'
        bytes = (Get-Item -LiteralPath $exe).Length
        sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'bin/updates/build-evidence.json') -Encoding utf8
    Write-Output "Native executable: $destination/NetLocate.exe"
}
finally { Pop-Location }
