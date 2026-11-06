#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Distribution,
    [string]$CdnRoot,
    [ValidateSet('stable','beta')][string]$Channel='stable',
    [string]$PublicKeyPath=(Join-Path (Split-Path $PSScriptRoot -Parent) 'QuicNet.Shared/Updates/release-public.pem')
)
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'CdnDiscovery.psm1') -Force
$Distribution=[IO.Path]::GetFullPath($Distribution)
$cdn=Resolve-CdnRoot -ExplicitPath $CdnRoot
$feed=if($Channel -eq 'beta'){'latest-beta.json'}else{'latest.json'}
$rsa=[Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem([IO.File]::ReadAllText($PublicKeyPath))
    function Read-SignedManifest([string]$Path) {
        $envelope=[IO.File]::ReadAllText($Path)|ConvertFrom-Json
        $payload=[Convert]::FromBase64String($envelope.payload)
        if(!$rsa.VerifyData($payload,[Convert]::FromBase64String($envelope.signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)){throw 'Firma de manifiesto inválida.'}
        return ([Text.Encoding]::UTF8.GetString($payload)|ConvertFrom-Json)
    }
    function Assert-File([string]$Path,$Metadata) {
        if($Metadata.size -le 0 -or $Metadata.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or !(Test-Path -LiteralPath $Path -PathType Leaf) -or
            (Get-Item -LiteralPath $Path).Length -ne $Metadata.size -or (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Metadata.sha256){throw "Tamaño/hash incorrecto: $Path"}
    }
    $manifestPath=Join-Path $Distribution $feed
    $manifest=Read-SignedManifest $manifestPath
    if($manifest.schema -ne 1 -or $manifest.channel -cne $Channel -or $manifest.version -notmatch '^\d+\.\d+\.\d+$' -or $manifest.build -lt 2){throw 'Identidad de manifiesto inválida.'}
    $releaseName="$($manifest.version)-$($manifest.build)$(if($Channel -eq 'beta'){'-beta'})"
    $releaseRelative="releases/$releaseName"
    $releaseDir=Join-Path $Distribution $releaseRelative
    $entries=@($manifest.artifacts.PSObject.Properties)
    if(!$entries.Count){throw 'No hay artefactos.'}
    foreach($entry in $entries) {
        $file=switch -CaseSensitive ($entry.Name){'win-x64'{'QuicNet-win-x64.exe'}'linux-x64'{'QuicNet-linux-x64.bin'}'android-arm64'{'QuicNet-android-arm64.apk'}default{throw 'Plataforma desconocida.'}}
        $artifact=$entry.Value
        if($artifact.url -cne "$releaseRelative/$file" -or $artifact.compressed.url -cne "$releaseRelative/$file.zst" -or
            $artifact.compressed.algorithm -cne 'zstd' -or $artifact.compressed.windowLog -lt 10 -or $artifact.compressed.windowLog -gt 27){throw 'Ruta o compresión de artefacto inválida.'}
        Assert-File (Join-Path $Distribution $artifact.url) $artifact
        Assert-File (Join-Path $Distribution $artifact.compressed.url) $artifact.compressed
    }
    foreach($name in @('latest.json','latest-beta.json')) {
        $existing=Join-Path $cdn $name
        if(Test-Path -LiteralPath $existing) {
            $previous=Read-SignedManifest $existing
            if($manifest.build -le $previous.build){throw "Build debe superar $($previous.build), publicado en $name."}
        }
    }
    $remoteRelease=Join-Path $cdn $releaseRelative
    foreach($file in Get-ChildItem -LiteralPath $releaseDir -File) {
        $existing=Join-Path $remoteRelease $file.Name
        if((Test-Path -LiteralPath $existing) -and (Get-FileHash -LiteralPath $existing).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Artefacto inmutable distinto: $existing"}
    }
    function Copy-Verified([string]$Source,[string]$Destination) {
        [IO.Directory]::CreateDirectory((Split-Path $Destination -Parent))|Out-Null
        $temporary=$Destination+'.'+[guid]::NewGuid().ToString('N')+'.partial'
        [IO.File]::Copy($Source,$temporary,$true)
        if((Get-FileHash -LiteralPath $Source).Hash -ne (Get-FileHash -LiteralPath $temporary).Hash){throw 'La copia del CDN no coincide.'}
        [IO.File]::Move($temporary,$Destination,$true)
    }
    foreach($file in Get-ChildItem -LiteralPath $releaseDir -File){Copy-Verified $file.FullName (Join-Path $remoteRelease $file.Name)}
    foreach($file in Get-ChildItem -LiteralPath $Distribution -Recurse -File) {
        $relative=[IO.Path]::GetRelativePath($Distribution,$file.FullName)
        if($relative -match '^releases[\\/]' -or $relative -in @('latest.json','latest-beta.json')){continue}
        Copy-Verified $file.FullName (Join-Path $cdn $relative)
    }
    foreach($entry in $entries) {
        Assert-File (Join-Path $cdn $entry.Value.url) $entry.Value
        Assert-File (Join-Path $cdn $entry.Value.compressed.url) $entry.Value.compressed
    }
    # Same-directory rename publishes the exact signed envelope only after every verified copy.
    Copy-Verified $manifestPath (Join-Path $cdn $feed)
    Write-Host "Publicado $($manifest.version) ($($manifest.build)), canal $Channel; paquetes preparados, firma y hashes verificados."
} finally {$rsa.Dispose()}
