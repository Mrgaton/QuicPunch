#requires -Version 7.0
param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][long]$Build,[string]$Channel='stable',[string]$Distribution='Ubuntu',[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(!$IsWindows){throw 'Este adaptador es para Windows/WSL. En Linux usa build-desktop-release.ps1 directamente.'}
$repo=Split-Path $PSScriptRoot -Parent
if(!$OutputDirectory){$OutputDirectory=Join-Path $repo ".artifacts/releases/$Version-$Build$(if($Channel -eq 'beta'){'-beta'})/linux"}
New-Item -ItemType Directory -Force $OutputDirectory|Out-Null
$linuxRepo=(& wsl.exe -d $Distribution --exec wslpath -a $repo).Trim()
$linuxOutput=(& wsl.exe -d $Distribution --exec wslpath -a ([IO.Path]::GetFullPath($OutputDirectory))).Trim()
$script="$linuxRepo/scripts/build-linux-wsl.sh"
& wsl.exe -d $Distribution --exec bash $script $linuxRepo $Version ([string]$Build) $Channel $linuxOutput
if($LASTEXITCODE -ne 0){throw 'Falló la compilación/prueba Linux en WSL.'}
