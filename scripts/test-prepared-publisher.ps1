#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Join-Path ([IO.Path]::GetTempPath()) ('quicnet-prepared-test-'+[guid]::NewGuid().ToString('N'))
$distribution=Join-Path $root 'distribution';$cdn=Join-Path $root 'cdn'
New-Item -ItemType Directory -Force $distribution,$cdn|Out-Null
$rsa=[Security.Cryptography.RSA]::Create(2048)
try {
    $public=Join-Path $root 'public.pem';[IO.File]::WriteAllText($public,$rsa.ExportSubjectPublicKeyInfoPem())
    function Prepare([long]$Build,[switch]$UnsafeUrl) {
        $directory=Join-Path $distribution "releases/9.0.0-$Build";New-Item -ItemType Directory -Force $directory|Out-Null
        $raw=Join-Path $directory 'QuicNet-win-x64.exe';$packed=$raw+'.zst'
        [IO.File]::WriteAllBytes($raw,[byte[]](1,2,3,4));[IO.File]::WriteAllBytes($packed,[byte[]](5,6,7))
        $url=if($UnsafeUrl){'../escape.exe'}else{"releases/9.0.0-$Build/QuicNet-win-x64.exe"}
        $payload=[Text.Encoding]::UTF8.GetBytes((@{schema=1;channel='stable';version='9.0.0';build=$Build;artifacts=@{'win-x64'=@{url=$url;size=4;sha256=(Get-FileHash $raw).Hash;compressed=@{algorithm='zstd';windowLog=27;url="$url.zst";size=3;sha256=(Get-FileHash $packed).Hash}}}}|ConvertTo-Json -Depth 8 -Compress))
        [IO.File]::WriteAllText((Join-Path $distribution 'latest.json'),(@{payload=[Convert]::ToBase64String($payload);signature=[Convert]::ToBase64String($rsa.SignData($payload,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1))}|ConvertTo-Json -Compress))
        [IO.File]::WriteAllText((Join-Path $distribution 'index.html'),'Fixture portal')
    }
    function Publish {& (Join-Path $PSScriptRoot 'publish-prepared-updates.ps1') -Distribution $distribution -CdnRoot $cdn -PublicKeyPath $public}
    function Reject([scriptblock]$Action) { $rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};if(!$rejected){throw 'La publicación inválida se aceptó.'} }
    Prepare 100
    [IO.File]::WriteAllText((Join-Path $distribution 'releases/9.0.0-100/QuicNet-win-x64.exe'),'tampered')
    Reject {Publish};if(Test-Path (Join-Path $cdn 'latest.json')){throw 'Se publicó un paquete alterado.'}
    Prepare 100 -UnsafeUrl;Reject {Publish}
    Prepare 100
    $envelope=Get-Content (Join-Path $distribution 'latest.json') -Raw|ConvertFrom-Json;$envelope.signature=[Convert]::ToBase64String([byte[]](1,2,3));$envelope|ConvertTo-Json -Compress|Set-Content (Join-Path $distribution 'latest.json')
    Reject {Publish}
    Prepare 100;Publish
    if((Get-FileHash (Join-Path $distribution 'latest.json')).Hash -ne (Get-FileHash (Join-Path $cdn 'latest.json')).Hash){throw 'Se modificó el manifiesto firmado.'}
    Reject {Publish}
    Prepare 101
    $future=Join-Path $cdn 'releases/9.0.0-101';New-Item -ItemType Directory -Force $future|Out-Null
    [IO.File]::WriteAllText((Join-Path $future 'QuicNet-win-x64.exe'),'immutable-conflict');Reject {Publish}
    $current=Get-Content (Join-Path $cdn 'latest.json') -Raw|ConvertFrom-Json
    if(([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($current.payload))|ConvertFrom-Json).build -ne 100){throw 'Un fallo reemplazó la release anterior.'}
    Write-Host 'PASS prepared publisher: signed identity, hashes, path rejection, exact bytes, downgrade/immutable guards and manifest-last failure safety.'
} finally {
    $rsa.Dispose();$resolved=[IO.Path]::GetFullPath($root)
    if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))){throw 'Directorio temporal fuera del espacio esperado.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
