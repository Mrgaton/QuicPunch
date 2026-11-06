#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64')][string]$Platform,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][long]$Build,
    [ValidateSet('stable','beta')][string]$Channel='stable',
    [string]$OutputDirectory,
    [string]$DotNetSdkVersion='11.0.100-rc.1.26425.128',
    [string]$CatalogPython
)
$ErrorActionPreference='Stop'
if (($Platform -eq 'win-x64') -ne $IsWindows) { throw 'Native AOT debe compilarse en el sistema de destino. Usa Windows/WSL o importa una distribución firmada del otro equipo.' }
$repo=Split-Path $PSScriptRoot -Parent
if (!$CatalogPython) { $CatalogPython = if ($IsWindows) { 'python' } else { 'python3' } }
& $CatalogPython (Join-Path $repo 'scripts/prepare-geolocation.py')
if ($LASTEXITCODE -ne 0) { throw 'No se pudieron preparar/verificar los catálogos compactos de geolocalización.' }
if (!$OutputDirectory) { $OutputDirectory=Join-Path $repo ".artifacts/releases/$Version-$Build$(if($Channel -eq 'beta'){'-beta'})/$(if($Platform -eq 'win-x64'){'desktop'}else{'linux'})" }
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
$work=Split-Path $OutputDirectory -Parent
$native=Join-Path $work "native-$Platform"
$dependencies=Join-Path $work "dependencies-$Platform"
$sdkHost=Join-Path $work "sdk-host-$Platform"
New-Item -ItemType Directory -Force $native,$sdkHost | Out-Null
[IO.File]::WriteAllText((Join-Path $sdkHost 'global.json'),(@{sdk=@{version=$DotNetSdkVersion;rollForward='disable';allowPrerelease=$true}}|ConvertTo-Json))
$common=@("-p:ReleaseVersion=$Version","-p:ReleaseNumber=$Build","-p:ReleaseChannel=$Channel","-p:BaseOutputPath=bin/updates/",'-p:NuGetAudit=false')
function Run-DotNet([string[]]$Arguments) {
    Push-Location $sdkHost
    try { & dotnet @Arguments; if($LASTEXITCODE -ne 0){throw "dotnet falló ($LASTEXITCODE)."} } finally {Pop-Location}
}
$project=Join-Path $repo 'QuicNet/QuicNet.csproj'
Run-DotNet (@('publish',$project,'-r',$Platform,'-c','Release','--self-contained','true','-p:PublishAot=false','-o',$dependencies,'-v','minimal')+$common)
$names=if($Platform -eq 'win-x64'){@('msquic.dll','e_sqlite3.dll','Photino.Native.dll','WebView2Loader.dll','wintun.dll')}else{@('libmsquic.so','libe_sqlite3.so','Photino.Native.so')}
foreach($name in $names){Copy-Item -LiteralPath (Join-Path $dependencies $name) -Destination (Join-Path $native $name) -Force}
Run-DotNet (@('publish',$project,'-r',$Platform,'-c','Release','-p:PublishAot=true',"-p:NativeBundleDirectory=$native",'-o',$OutputDirectory,'-v','minimal')+$common)
$binary=Join-Path $OutputDirectory $(if($Platform -eq 'win-x64'){'QuicNet.exe'}else{'QuicNet'})
if((& $binary --version |Select-Object -Last 1) -cne "$Version ($Build) AOT=True"){throw 'Identidad del binario incorrecta.'}
if((& $binary --version --channel |Select-Object -Last 1) -cne $Channel){throw 'Canal del binario incorrecto.'}
$smoke=Join-Path $work "single-file-test-$Platform"
New-Item -ItemType Directory -Force $smoke |Out-Null
$test=Join-Path $smoke ([IO.Path]::GetFileName($binary))
Copy-Item -LiteralPath $binary -Destination $test -Force
& $test --release-smoke-test
if($LASTEXITCODE -ne 0){throw 'La distribución Native AOT no superó la prueba aislada.'}
$identity=@{platform=$Platform;version=$Version;build=$Build;channel=$Channel;sha256=(Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()}
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'build-info.json'),($identity|ConvertTo-Json -Compress))
Write-Host "Binario verificado: $binary"
