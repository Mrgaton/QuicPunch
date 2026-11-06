#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [ValidateRange(2, 2100000000)][long]$Build,
    [ValidateSet('stable', 'beta')][string]$Channel = 'stable',
    [string]$CdnRoot,
    [string[]]$Platforms,
    [string]$LinuxArtifact,
    [string]$WindowsArtifact,
    [switch]$WithLinux,
    [string]$WslDistribution='Ubuntu',
    [string]$SigningKey,
    [string]$AndroidKeyStore,
    [string]$AndroidKeyAlias = 'androiddebugkey',
    [string]$DotNetSdkVersion = '11.0.100-rc.1.26425.128',
    [string]$AndroidSdkDirectory,
    [string]$JavaSdkDirectory,
    [switch]$LocalOnly,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$Channel = $Channel.ToLowerInvariant()
$repo = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $repo
Import-Module (Join-Path $PSScriptRoot 'CdnDiscovery.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'ReleaseSecrets.psm1') -Force
if (!$Platforms) { $Platforms=@($(if($IsWindows){'win-x64'}else{'linux-x64'}),'android-arm64') }
$Platforms=@($Platforms|ForEach-Object {$_.Split(',')}|Select-Object -Unique)
foreach($platform in $Platforms){if($platform -notin @('win-x64','linux-x64','android-arm64')){throw "Plataforma desconocida: $platform"}}
if ($LinuxArtifact -and 'linux-x64' -notin $Platforms) {$Platforms+='linux-x64'}
if ($WindowsArtifact -and 'win-x64' -notin $Platforms) {$Platforms+='win-x64'}
if (!$LocalOnly) { $CdnRoot=Resolve-CdnRoot -ExplicitPath $CdnRoot; Write-Host "CDN detectado: $CdnRoot" }
[xml]$currentProps=Get-Content -LiteralPath (Join-Path $repo 'Release.props')
$currentVersion=[version]$currentProps.Project.PropertyGroup.ReleaseVersion.InnerText
$currentBuild=[long]$currentProps.Project.PropertyGroup.ReleaseNumber.InnerText
if (!$Build) {
    $published=$currentBuild
    if($CdnRoot){foreach($name in @('latest.json','latest-beta.json')){ $path=Join-Path $CdnRoot $name; if(Test-Path -LiteralPath $path){$envelope=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json; $item=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($envelope.payload))|ConvertFrom-Json; $published=[Math]::Max($published,[long]$item.build); if([version]$item.version -gt $currentVersion){$currentVersion=[version]$item.version}} }}
    $Build=if($SkipBuild){$currentBuild}else{$published+1}
}
if (!$Version) {$Version=if($SkipBuild){$currentVersion.ToString()}else{"$($currentVersion.Major).$($currentVersion.Minor).$($currentVersion.Build+1)"}}
if (!$AndroidSdkDirectory) {$AndroidSdkDirectory=if($env:ANDROID_HOME){$env:ANDROID_HOME}elseif($IsWindows){Join-Path $env:LOCALAPPDATA 'Android/Sdk'}else{Join-Path $env:HOME 'Android/Sdk'}}
if (!$JavaSdkDirectory) {$JavaSdkDirectory=if($env:JAVA_HOME){$env:JAVA_HOME}elseif($IsWindows){Join-Path $env:LOCALAPPDATA 'Android/jdk-21'}else{'/usr/lib/jvm/java-21-openjdk-amd64'}}
if (!$SigningKey) { $SigningKey = Get-DefaultReleaseSigningKeyPath }
if (!$AndroidKeyStore) {
    $AndroidKeyStore = Get-DefaultAndroidSigningKeyStorePath
    if (!(Test-Path -LiteralPath $AndroidKeyStore)) {
        $AndroidKeyStore=if($IsWindows){Join-Path $env:LOCALAPPDATA 'Xamarin/Mono for Android/debug.keystore'}else{Join-Path $env:HOME '.android/debug.keystore'}
    }
}
$SigningKey = Resolve-PersistentReleaseSecretPath -Path $SigningKey -RepositoryRoot $repo -CdnRoot $CdnRoot
$AndroidKeyStore = Resolve-PersistentReleaseSecretPath -Path $AndroidKeyStore -RepositoryRoot $repo -CdnRoot $CdnRoot
if (!(Test-Path -LiteralPath $SigningKey)) { throw "Falta la clave privada de publicación: $SigningKey. Consulta doc/deployment/auto-updates.md." }
if ('android-arm64' -in $Platforms -and !(Test-Path -LiteralPath $AndroidKeyStore)) { throw 'Falta el keystore Android. Usa el MISMO que firmó la APK instalada.' }

$rsa = [Security.Cryptography.RSA]::Create()
$rsa.ImportFromPem([IO.File]::ReadAllText($SigningKey))
$public = [Security.Cryptography.RSA]::Create()
$public.ImportFromPem([IO.File]::ReadAllText((Join-Path $repo 'QuicNet.Shared/Updates/release-public.pem')))
if ($rsa.ExportSubjectPublicKeyInfoPem() -cne $public.ExportSubjectPublicKeyInfoPem()) { throw 'La clave privada no corresponde a la clave pública de los clientes.' }
$public.Dispose()

$releaseName = "$Version-$Build"
if ($Channel -eq 'beta') { $releaseName += '-beta' }
$manifestName = if ($Channel -eq 'beta') { 'latest-beta.json' } else { 'latest.json' }
$work = Join-Path $repo ".artifacts/releases/$releaseName"
$distribution = Join-Path $work 'cdn'
$native = Join-Path $work 'native'
$desktop = Join-Path $work 'desktop'
$android = Join-Path $work 'android'
$releaseDir = Join-Path $distribution "releases/$releaseName"
New-Item -ItemType Directory -Force $releaseDir, $native | Out-Null
if($WithLinux -and $IsWindows -and !$LinuxArtifact) {
    $LinuxArtifact=Join-Path $work 'linux'
    if(!$SkipBuild){& (Join-Path $PSScriptRoot 'build-linux-wsl.ps1') -Version $Version -Build $Build -Channel $Channel -Distribution $WslDistribution -OutputDirectory $LinuxArtifact}
    if('linux-x64' -notin $Platforms){$Platforms+='linux-x64'}
}
$sdkHost = Join-Path $work 'sdk-host'
New-Item -ItemType Directory -Force $sdkHost | Out-Null
[IO.File]::WriteAllText((Join-Path $sdkHost 'global.json'), (@{sdk=@{version=$DotNetSdkVersion;rollForward='disable';allowPrerelease=$true}} | ConvertTo-Json))
function Invoke-DotNet([string[]]$Arguments) {
    # Keep the selected SDK/workloads stable even when Visual Studio installs a newer preview.
    $Arguments = @($Arguments | ForEach-Object { if ($_.EndsWith('.csproj')) { Join-Path $repo $_ } else { $_ } })
    Push-Location -LiteralPath $sdkHost
    try {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet falló ($LASTEXITCODE). No se publica el manifiesto." }
    } finally { Pop-Location }
}

if (!$SkipBuild) {
    [xml]$props = Get-Content -LiteralPath 'Release.props'
    $props.Project.PropertyGroup.ReleaseVersion.InnerText = $Version
    $props.Project.PropertyGroup.ReleaseNumber.InnerText = [string]$Build
    $props.Save((Join-Path $repo 'Release.props'))
    $common = @("-p:ReleaseVersion=$Version", "-p:ReleaseNumber=$Build", "-p:ReleaseChannel=$Channel", "-p:BaseOutputPath=bin/updates/", '-p:NuGetAudit=false')
    foreach($platform in @('win-x64','linux-x64')) {
        if($platform -notin $Platforms){continue}
        $import=if($platform -eq 'win-x64'){$WindowsArtifact}else{$LinuxArtifact}
        if($import){continue}
        if(($platform -eq 'win-x64') -ne $IsWindows){throw "Para $platform importa su carpeta Native AOT con -WindowsArtifact o -LinuxArtifact. Native AOT se compila en el sistema de destino."}
        $output=if($platform -eq 'win-x64'){$desktop}else{Join-Path $work 'linux'}
        & (Join-Path $PSScriptRoot 'build-desktop-release.ps1') -Platform $platform -Version $Version -Build $Build -Channel $Channel -OutputDirectory $output -DotNetSdkVersion $DotNetSdkVersion
    }
    if('android-arm64' -in $Platforms) {
    # Existing installations are debug-signed: preserve this identity by default.
    # For a custom production key, set QUICNET_ANDROID_STORE_PASS / QUICNET_ANDROID_KEY_PASS.
    if (!$env:QUICNET_ANDROID_STORE_PASS) { $env:QUICNET_ANDROID_STORE_PASS = 'android' }
    if (!$env:QUICNET_ANDROID_KEY_PASS) { $env:QUICNET_ANDROID_KEY_PASS = $env:QUICNET_ANDROID_STORE_PASS }
    Invoke-DotNet (@('publish', 'QuicPunch.AndroidProbe/QuicPunch.AndroidProbe.csproj', '-c', 'Release',
        "-p:AndroidSdkDirectory=$AndroidSdkDirectory", "-p:JavaSdkDirectory=$JavaSdkDirectory",
        '-p:AndroidKeyStore=true', "-p:AndroidSigningKeyStore=$AndroidKeyStore", "-p:AndroidSigningKeyAlias=$AndroidKeyAlias",
        '-p:AndroidSigningStorePass=env:QUICNET_ANDROID_STORE_PASS', '-p:AndroidSigningKeyPass=env:QUICNET_ANDROID_KEY_PASS',
        '-p:AndroidPackageFormats=apk', '-o', $android, '-v', 'minimal') + $common)
}
}
$artifacts=[ordered]@{}
$cachedArtifacts=$null
$cachedManifest=Join-Path $distribution $manifestName
if(Test-Path -LiteralPath $cachedManifest){
    $cachedEnvelope=Get-Content -LiteralPath $cachedManifest -Raw|ConvertFrom-Json
    $cachedPayload=[Convert]::FromBase64String($cachedEnvelope.payload)
    if($rsa.VerifyData($cachedPayload,[Convert]::FromBase64String($cachedEnvelope.signature),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)){
        $cached=[Text.Encoding]::UTF8.GetString($cachedPayload)|ConvertFrom-Json
        if($cached.version -ceq $Version -and $cached.build -eq $Build -and $cached.channel -ceq $Channel){$cachedArtifacts=$cached.artifacts}
    }
}
foreach($platform in $Platforms) {
    $fileName=switch($platform){'win-x64'{'QuicNet-win-x64.exe'}'linux-x64'{'QuicNet-linux-x64.bin'}'android-arm64'{'QuicNet-android-arm64.apk'}}
    if($platform -eq 'android-arm64') {
        $apk=Get-ChildItem -LiteralPath $android -Recurse -Filter '*-Signed.apk'|Select-Object -First 1
        if(!$apk){throw 'Falta la APK firmada.'}
        $aaptName=if($IsWindows){'aapt.exe'}else{'aapt'}
        $aapt=Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Recurse -Filter $aaptName|Sort-Object FullName -Descending|Select-Object -First 1
        if(!$aapt){throw 'No se encuentra aapt para verificar la APK.'}
        $apkIdentity=(& $aapt.FullName dump badging $apk.FullName|Select-Object -First 1)
        $apkVersion=if($Channel -eq 'beta'){"$Version-beta"}else{$Version}
        if($LASTEXITCODE -ne 0 -or $apkIdentity -notmatch "name='org.quicpunch.androidprobe'.*versionCode='$Build'.*versionName='$([regex]::Escape($apkVersion))'"){throw "Identidad de APK incorrecta: $apkIdentity"}
        $apksigner=Get-ChildItem -LiteralPath (Join-Path $AndroidSdkDirectory 'build-tools') -Recurse -Filter 'apksigner.jar'|Sort-Object FullName -Descending|Select-Object -First 1
        $java=Join-Path $JavaSdkDirectory "bin/$(if($IsWindows){'java.exe'}else{'java'})"
        $certificate=& $java -jar $apksigner.FullName verify --print-certs $apk.FullName
        if($LASTEXITCODE -ne 0){throw 'Firma de APK inválida.'}
        $expected=(Get-Content -LiteralPath (Join-Path $repo 'QuicPunch.AndroidProbe/signing-certificate.sha256') -Raw).Trim()
        if(($certificate|Where-Object {$_ -match 'certificate SHA-256 digest:'}) -notmatch [regex]::Escape($expected)){throw 'La clave Android no coincide con las instalaciones existentes. Importa el keystore original; no se publica.'}
        $source=$apk.FullName
    } else {
        $dir=if($platform -eq 'win-x64'){if($WindowsArtifact){$WindowsArtifact}else{$desktop}}else{if($LinuxArtifact){$LinuxArtifact}else{Join-Path $work 'linux'}}
        $source=Join-Path $dir $(if($platform -eq 'win-x64'){'QuicNet.exe'}else{'QuicNet'})
        $info=Get-Content -LiteralPath (Join-Path $dir 'build-info.json') -Raw|ConvertFrom-Json
        if($info.platform -cne $platform -or $info.version -cne $Version -or $info.build -ne $Build -or $info.channel -cne $Channel -or $info.sha256 -ine (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash){throw "Artefacto importado incorrecto: $source"}
        if(($platform -eq 'win-x64') -eq $IsWindows){
            if((& $source --version|Select-Object -Last 1) -cne "$Version ($Build) AOT=True"){throw 'Identidad del binario incorrecta.'}
            & $source --release-smoke-test
            if($LASTEXITCODE -ne 0){throw 'Falló la prueba funcional Native AOT.'}
        }
    }
    $destination=Join-Path $releaseDir $fileName
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $file=Get-Item -LiteralPath $destination
    $zstd=$destination+'.zst'
    $rawHash=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
    $cached=if($cachedArtifacts){$cachedArtifacts.$platform}else{$null}
    $reuse=$cached -and $cached.sha256 -ceq $rawHash -and $cached.compressed.algorithm -ceq 'zstd' -and $cached.compressed.windowLog -eq 27 -and (Test-Path -LiteralPath $zstd) -and (Get-Item -LiteralPath $zstd).Length -eq $cached.compressed.size
    if($reuse){$reuse=(Get-FileHash -LiteralPath $zstd -Algorithm SHA256).Hash -ieq $cached.compressed.sha256}
    if(!$reuse){Invoke-DotNet @('run','--project','QuicNet.ReleaseTools/QuicNet.ReleaseTools.csproj','-c','Release','--','compress',$destination,$zstd)}
    $packed=Get-Item -LiteralPath $zstd
    $artifacts[$platform]=[ordered]@{url="releases/$releaseName/$fileName";sha256=(Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant();size=$file.Length;compressed=[ordered]@{algorithm='zstd';url="releases/$releaseName/$fileName.zst";sha256=(Get-FileHash -LiteralPath $zstd -Algorithm SHA256).Hash.ToLowerInvariant();size=$packed.Length;windowLog=27}}
}
foreach($asset in Get-ChildItem -LiteralPath (Join-Path $repo 'scripts/download-page') -Recurse -File){
    $relative=[IO.Path]::GetRelativePath((Join-Path $repo 'scripts/download-page'),$asset.FullName)
    $target=Join-Path $distribution $relative
    New-Item -ItemType Directory -Force (Split-Path $target -Parent)|Out-Null
    Copy-Item -LiteralPath $asset.FullName -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $repo 'QuicNet.Shared/Updates/release-public.pem') -Destination (Join-Path $distribution 'release-public.pem') -Force
$pageFile=Join-Path $distribution 'index.html'
$assetVersion=$releaseName+'-'+(Get-FileHash -LiteralPath (Join-Path $distribution 'download.js') -Algorithm SHA256).Hash.Substring(0,12)
$assetPrefix="download-assets/$assetVersion"
foreach($asset in Get-ChildItem -LiteralPath (Join-Path $repo 'scripts/download-page') -Recurse -File|Where-Object Name -NE 'index.html'){
    $relative=[IO.Path]::GetRelativePath((Join-Path $repo 'scripts/download-page'),$asset.FullName)
    $target=Join-Path (Join-Path $distribution $assetPrefix) $relative
    New-Item -ItemType Directory -Force (Split-Path $target -Parent)|Out-Null
    Copy-Item -LiteralPath $asset.FullName -Destination $target -Force
}
[IO.File]::WriteAllText($pageFile,[IO.File]::ReadAllText($pageFile).Replace('__ASSET_PREFIX__',$assetPrefix),[Text.UTF8Encoding]::new($false))
$payload = [Text.Encoding]::UTF8.GetBytes(([ordered]@{ schema = 1; channel = $Channel; version = $Version; build = $Build;
    publishedAt = [DateTimeOffset]::UtcNow.ToString('O'); artifacts = $artifacts } | ConvertTo-Json -Depth 8 -Compress))
$signature = $rsa.SignData($payload, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
$rsa.Dispose()
$envelope = [ordered]@{ payload = [Convert]::ToBase64String($payload); signature = [Convert]::ToBase64String($signature) } | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $distribution $manifestName), $envelope, [Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath 'doc/deployment/auto-updates.md' -Destination (Join-Path $distribution 'PUBLISHING.md') -Force
Copy-Item -LiteralPath 'doc/deployment/auto-updates.md' -Destination (Join-Path $releaseDir 'PUBLISHING.md') -Force
Copy-Item -LiteralPath 'QuicNet.Geolocation/Data/NOTICE.txt' -Destination (Join-Path $releaseDir 'GEOLOCATION-NOTICE.txt') -Force
Copy-Item -LiteralPath 'QuicNet.Geolocation/Map/NOTICE.txt' -Destination (Join-Path $releaseDir 'MAP-NOTICE.txt') -Force
Write-Host "Distribución lista: $distribution"
if ($LocalOnly) { Write-Host 'Solo preparación local: no se ha escrito en el CDN.'; return }

# Never replace the manifest until BOTH immutable artifacts have been uploaded and hashed.
$cdn = [IO.Path]::GetFullPath($CdnRoot)
if ($cdn -eq [IO.Path]::GetPathRoot($cdn)) { throw 'CdnRoot no puede ser la raíz de una unidad.' }
New-Item -ItemType Directory -Force $cdn | Out-Null
$remoteManifest = Join-Path $cdn $manifestName
# Android versionCode and desktop rollback protection require globally increasing builds.
foreach ($existingName in @('latest.json', 'latest-beta.json')) {
    $existingManifest = Join-Path $cdn $existingName
    if (!(Test-Path -LiteralPath $existingManifest)) { continue }
    $previousEnvelope = Get-Content -LiteralPath $existingManifest -Raw | ConvertFrom-Json
    $previousPayload = [Convert]::FromBase64String($previousEnvelope.payload)
    $previousPublic = [Security.Cryptography.RSA]::Create()
    $previousPublic.ImportFromPem([IO.File]::ReadAllText((Join-Path $repo 'QuicNet.Shared/Updates/release-public.pem')))
    if (!$previousPublic.VerifyData($previousPayload, [Convert]::FromBase64String($previousEnvelope.signature),
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw 'El manifiesto existente tiene una firma inválida.' }
    $previousPublic.Dispose()
    $previous = [Text.Encoding]::UTF8.GetString($previousPayload) | ConvertFrom-Json
    if ($Build -le $previous.build) { throw "Build debe ser mayor que el publicado en $existingName ($($previous.build))." }
}
$remoteRelease = Join-Path $cdn "releases/$releaseName"
if (Test-Path -LiteralPath $remoteRelease) {
    # An interrupted upload can be resumed; published versions must never be overwritten.
    foreach ($file in Get-ChildItem -LiteralPath $releaseDir -File) {
        $remoteFile = Join-Path $remoteRelease $file.Name
        if ((Test-Path -LiteralPath $remoteFile) -and (Get-FileHash -LiteralPath $remoteFile).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw "Una carpeta de versión existente contiene bytes distintos: $remoteFile. Incrementa Build."
        }
    }
}
New-Item -ItemType Directory -Force $remoteRelease | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $releaseDir -File) {
    $temporary = Join-Path $remoteRelease ($file.Name + '.partial')
    $destination = Join-Path $remoteRelease $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $temporary -Force
    if ((Get-FileHash -LiteralPath $temporary).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw 'La copia del CDN no coincide.' }
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
foreach($item in Get-ChildItem -LiteralPath $distribution -Recurse -File) {
    $relative=[IO.Path]::GetRelativePath($distribution,$item.FullName)
    if($relative.StartsWith('releases') -or $relative -in @('latest.json','latest-beta.json')){continue}
    $destination=Join-Path $cdn $relative
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent)|Out-Null
    $temporary=$destination+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    Copy-Item -LiteralPath $item.FullName -Destination $temporary -Force
    Move-Item -LiteralPath $temporary -Destination $destination -Force
}
$manifestTemporary = Join-Path $cdn ($manifestName + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
[IO.File]::WriteAllText($manifestTemporary, $envelope, [Text.UTF8Encoding]::new($false))
# Same-directory rename is atomic on the hosting filesystem; no missing/truncated latest.json window.
[IO.File]::Move($manifestTemporary, $remoteManifest, $true)
Write-Host "Publicado $Version ($Build), canal ${Channel}: https://cdn.gato.ovh/QuicNet/$manifestName"
