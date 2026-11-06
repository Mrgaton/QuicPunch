#requires -Version 7.0
$ErrorActionPreference='Stop'
Import-Module (Join-Path $PSScriptRoot 'CdnDiscovery.psm1') -Force
$temp=Join-Path ([IO.Path]::GetTempPath()) ('quicnet-cdn-test-'+[guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force (Join-Path $temp 'mounted-one/CDN/QuickPunch') |Out-Null
    $one=Resolve-CdnRoot -Roots @((Join-Path $temp 'mounted-one'))
    if($one -ne (Join-Path $temp 'mounted-one/CDN/QuickPunch')){throw 'No detectó CDN/QuickPunch.'}
    if((Resolve-CdnRoot -Roots @((Join-Path $temp 'mounted-one/CDN'))) -ne $one){throw 'No detectó un montaje de CDN.'}
    New-Item -ItemType Directory -Force (Join-Path $temp 'mounted-two/CDN/QuicNet')|Out-Null
    $rejected=$false
    try{Resolve-CdnRoot -Roots @((Join-Path $temp 'mounted-one'),(Join-Path $temp 'mounted-two'))|Out-Null}catch{$rejected=$true}
    if(!$rejected){throw 'Eligió arbitrariamente entre dos CDN.'}
    if((Resolve-CdnRoot -ExplicitPath $one) -ne $one){throw 'Ignoró la selección explícita.'}
    Write-Host 'PASS CDN discovery: mounted roots, QuickPunch/QuicNet, ambiguity and explicit path.'
} finally {
    $resolved=[IO.Path]::GetFullPath($temp)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))){throw 'Ruta temporal fuera del directorio esperado.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
