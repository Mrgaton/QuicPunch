#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('stable','beta')][string]$Channel='stable',
    [string]$CdnRoot,
    [string]$DotNetSdkVersion='11.0.100',
    [string]$SigningKey,
    [string]$AndroidKeyStore,
    [string]$WslDistribution='Ubuntu'
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repo
Import-Module (Join-Path $PSScriptRoot 'CdnDiscovery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ReleaseSecrets.psm1') -Force
if(!$IsWindows){throw 'Esta utilidad publica las tres plataformas desde Windows con Ubuntu en WSL.'}
$cdn=Resolve-CdnRoot -ExplicitPath $CdnRoot
if(!$SigningKey){
    $SigningKey=Get-DefaultReleaseSigningKeyPath
    if(!(Test-Path -LiteralPath $SigningKey)){$SigningKey=Join-Path $env:USERPROFILE '.quicnet-release-backup/release-private.pem'}
}
if(!$AndroidKeyStore){
    $AndroidKeyStore=Get-DefaultAndroidSigningKeyStorePath
    if(!(Test-Path -LiteralPath $AndroidKeyStore)){$AndroidKeyStore=Join-Path $env:USERPROFILE '.quicnet-release-backup/android-release.keystore'}
}
foreach($path in @($SigningKey,$AndroidKeyStore)){if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Falta una identidad de firma existente: $path. Consulta doc/deployment/auto-updates.md."}}
[xml]$props=Get-Content -LiteralPath (Join-Path $repo 'Release.props')
$version=[version]$props.Project.PropertyGroup.ReleaseVersion.InnerText
$build=[long]$props.Project.PropertyGroup.ReleaseNumber.InnerText
foreach($name in @('latest.json','latest-beta.json')){
    $path=Join-Path $cdn $name
    if(Test-Path -LiteralPath $path){
        $envelope=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
        $item=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload))|ConvertFrom-Json
        if([version]$item.version -gt $version){$version=[version]$item.version}
        $build=[Math]::Max($build,[long]$item.build)
    }
}
$version="$( $version.Major ).$( $version.Minor ).$( $version.Build + 1 )"
$build++
$release="$version-$build$(if($Channel -eq 'beta'){'-beta'})"
Write-Host "Preparando $release ($Channel) para $cdn"
function Run-Checked([string]$Command,[string[]]$Arguments){
    & $Command @Arguments
    if($LASTEXITCODE -ne 0){throw "$Command falló ($LASTEXITCODE). No se publicará la versión."}
}
Run-Checked dotnet @('run','-c','Release','--project','QuicPunchTests','--','--test-updates')
Run-Checked npm.cmd @('run','test:web')
Run-Checked npm.cmd @('run','test:browser')
& (Join-Path $PSScriptRoot 'publish-updates.ps1') -Version $version -Build $build -Channel $Channel -WithLinux -WslDistribution $WslDistribution -LocalOnly -DotNetSdkVersion $DotNetSdkVersion -SigningKey $SigningKey -AndroidKeyStore $AndroidKeyStore
$distribution=Join-Path $repo ".artifacts/releases/$release/cdn"
& (Join-Path $PSScriptRoot 'publish-prepared-updates.ps1') -Distribution $distribution -CdnRoot $cdn -Channel $Channel
& (Join-Path $PSScriptRoot 'verify-published-release.ps1') -Version $version -Build $build -Channel $Channel
Write-Host "Publicación verificada: $version (Build $build), $Channel."
