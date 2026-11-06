#requires -Version 7.0
[CmdletBinding(SupportsShouldProcess)]
param(
    # Download and validate the production runtime without installing the driver.
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
if (!$IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'The pinned MsQuic XDP datapath requires Windows x64. Linux/Android use OS UDP sockets.'
}
if (!$PrepareOnly -and ![Security.Principal.WindowsPrincipal]::new(
    [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an Administrator PowerShell to install the XDP network driver. Use -PrepareOnly to validate without elevation.'
}

# Same production release as the pinned MsQuic scripts/xdp.json. Never enable
# test signing, disable Secure Boot, or install a test-signed prerelease.
$version = '1.4.0'
$sha256 = 'F78F091C3EDA1D17D2BF4F7E0DDC593FBEA1DAAC6A53CA69DE2F1854E8070D23'
$uri = "https://api.nuget.org/v3-flatcontainer/microsoft.xdp-for-windows.runtime.x64/$version/microsoft.xdp-for-windows.runtime.x64.$version.nupkg"
$repo = Split-Path $PSScriptRoot -Parent
$staging = Join-Path $repo ".artifacts/xdp-runtime/$version-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $staging -Force | Out-Null
$package = Join-Path $staging 'runtime.nupkg'
Invoke-WebRequest -Uri $uri -OutFile $package
if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $sha256) {
    throw 'XDP runtime package hash mismatch.'
}
Expand-Archive -LiteralPath $package -DestinationPath (Join-Path $staging 'package')
$native = Join-Path $staging 'package/runtime/native'
foreach ($name in 'xdp.cat', 'xdp.sys') {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $native $name)
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation,') {
        throw "XDP production signature verification failed for $name ($($signature.Status))."
    }
}
Write-Output "Verified Microsoft XDP $version production package and driver signatures."
if ($PrepareOnly) {
    Write-Output 'Preparation complete; no driver installed.'
    return
}
if ($PSCmdlet.ShouldProcess('Windows network adapters', "Install Microsoft XDP $version driver")) {
    & (Join-Path $native 'xdp-setup.ps1') -Install xdp -Verbose
    $service = Get-Service -Name xdp -ErrorAction Stop
    Write-Output "XDP service: $($service.Status). Restart QuicNet so MsQuic discovers the new driver."
    Write-Output 'XDP needs permission to program adapter rules. For direct rule programming, launch QuicNet elevated. Unsupported routes still fall back to OS sockets.'
}
