#requires -Version 7.0
[CmdletBinding()]param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][long]$Build,[ValidateSet('stable','beta')][string]$Channel='stable',[string]$BaseUrl='https://cdn.gato.ovh/QuicNet/')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$release="$Version-$Build$(if($Channel -eq 'beta'){'-beta'})"
$directory=Join-Path $repo ".artifacts/releases/$release/http-verification"
New-Item -ItemType Directory -Force $directory|Out-Null
$feed=if($Channel -eq 'beta'){'latest-beta.json'}else{'latest.json'}
$base=[uri]$BaseUrl
if($base.Scheme -ne 'https'){throw 'La verificación pública requiere HTTPS.'}
$manifestFile=Join-Path $directory $feed
Invoke-WebRequest -Uri ([uri]::new($base,($feed+'?check='+[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))) -OutFile $manifestFile -TimeoutSec 60
$envelope=Get-Content -LiteralPath $manifestFile -Raw|ConvertFrom-Json
$payload=[Convert]::FromBase64String($envelope.payload)
$rsa=[Security.Cryptography.RSA]::Create()
try{
    $rsa.ImportFromPem([IO.File]::ReadAllText((Join-Path $repo 'QuicNet.Shared/Updates/release-public.pem')))
    if(!$rsa.VerifyData($payload,[Convert]::FromBase64String($envelope.signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)){throw 'Firma del manifiesto inválida.'}
}finally{$rsa.Dispose()}
$manifest=[Text.Encoding]::UTF8.GetString($payload)|ConvertFrom-Json
if($manifest.version -cne $Version -or $manifest.build -ne $Build -or $manifest.channel -cne $Channel){throw 'El CDN no devuelve la release esperada.'}
foreach($entry in $manifest.artifacts.PSObject.Properties){
    $artifact=$entry.Value
    foreach($variant in @($artifact,$artifact.compressed)){
        $url=[uri]::new($base,[string]$variant.url)
        if($url.Scheme -ne 'https' -or $url.Authority -ne $base.Authority -or !$url.AbsolutePath.StartsWith($base.AbsolutePath+"releases/$release/") -or $url.Query -or $url.Fragment){throw 'URL de artefacto insegura.'}
        $file=Join-Path $directory ([IO.Path]::GetFileName($url.AbsolutePath))
        Invoke-WebRequest -Uri $url -OutFile $file -TimeoutSec 300
        if((Get-Item -LiteralPath $file).Length -ne $variant.size -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $variant.sha256){throw "Tamaño/hash público incorrecto: $url"}
    }
    $packed=Join-Path $directory ([IO.Path]::GetFileName($artifact.compressed.url))
    $unpacked=Join-Path $directory ([IO.Path]::GetFileName($artifact.url)+'.verified')
    & dotnet run --project (Join-Path $repo 'QuicNet.ReleaseTools/QuicNet.ReleaseTools.csproj') -c Release --no-build -- decompress $packed $unpacked ([string]$artifact.size)
    if($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $unpacked -Algorithm SHA256).Hash -ine $artifact.sha256){throw 'La descompresión pública no coincide.'}
    Write-Host "VERIFIED $($entry.Name): HTTPS, raw/Zstandard sizes and hashes, decompressed hash."
}
$page=Invoke-WebRequest -Uri ([uri]::new($base,('index.html?check='+[DateTimeOffset]::UtcNow.ToUnixTimeSeconds()))) -TimeoutSec 60
if(!$page.Content.Contains('Descargar QuicNet')){throw 'Falta la página de descarga.'}
$scriptMatch=[regex]::Match($page.Content,'src="([^"]*download\.js[^"]*)"')
if(!$scriptMatch.Success){throw 'Falta el módulo de descarga.'}
$scriptPath=$scriptMatch.Groups[1].Value
$assetDirectory=$scriptPath.Substring(0,$scriptPath.LastIndexOf('/')+1)
foreach($asset in @($scriptPath,($assetDirectory+'decompress-worker.js'),($assetDirectory+'vendor/zstddec-0.3.1.js'))){
    $response=Invoke-WebRequest -Uri ([uri]::new($base,$asset)) -TimeoutSec 60
    if($response.Headers['Content-Type'] -notmatch 'javascript|ecmascript'){throw "MIME JavaScript incorrecto para $asset"}
}
Write-Host "VERIFIED published $Version ($Build) $Channel, RSA signature and download page."
