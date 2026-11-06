#requires -Version 5.1
<#
.SYNOPSIS
Elimina archivos regenerables del proyecto y muestra el espacio recuperado.
.EXAMPLE
.\scripts\clean-project.ps1 -WhatIf
.EXAMPLE
.\scripts\clean-project.ps1
.NOTES
Cierra Visual Studio y las aplicaciones del proyecto antes de limpiar.
Elimina .artifacts y carpetas .codex-* que solo contienen bin/obj.
Estas carpetas solo deben contener archivos temporales o regenerables.
Conserva artifacts, backups,
bibliotecas nativas, datos de la aplicacion y caches globales de NuGet/SDK.
La siguiente compilacion regenerara bin/obj y puede necesitar Internet.
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$rootPrefix = $root + [IO.Path]::DirectorySeparatorChar
$targets = New-Object 'System.Collections.Generic.List[string]'
$pending = New-Object 'System.Collections.Generic.Stack[string]'
$pending.Push($root)

function Assert-LocalPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Ruta fuera del proyecto: $Path"
    }
    # No atravesar enlaces simbolicos ni junctions, tampoco en los padres.
    $current = $fullPath
    while ($current) {
        $item = Get-Item -LiteralPath $current -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Se conserva la ruta porque contiene un enlace: $current"
        }
        $current = Split-Path -Parent $current
    }
}

function Get-LocalSize([string] $Path) {
    $directories = New-Object 'System.Collections.Generic.Stack[string]'
    $directories.Push($Path)
    [long] $bytes = 0
    while ($directories.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $directories.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Se conserva la carpeta porque contiene un enlace: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $directories.Push($item.FullName) }
            else { $bytes += $item.Length }
        }
    }
    return $bytes
}

# No usar git clean: los archivos ignorados tambien pueden ser datos importantes.
# Proteger cualquier archivo versionado que aparezca dentro de una carpeta candidata.
$tracked = @()
if (Test-Path -LiteralPath (Join-Path $root '.git')) {
    $tracked = @(& git -C $root -c core.quotepath=false ls-files)
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron consultar los archivos versionados.' }
}

while ($pending.Count -gt 0) {
    $directory = $pending.Pop()
    $children = @(Get-ChildItem -LiteralPath $directory -Force)
    foreach ($child in $children) {
        if (-not $child.PSIsContainer -or
            ($child.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }

        $isBuild = $child.Name -in @('bin', 'obj')
        $isCache = $child.Name -in @('.vs', '.codex-build', '.artifacts', 'TestResults', 'BenchmarkDotNet.Artifacts')
        $isCodexBuild = $false
        if ($child.Name -like '.codex-*' -and -not $isCache) {
            $codexChildren = @(Get-ChildItem -LiteralPath $child.FullName -Force)
            $isCodexBuild = @($codexChildren | Where-Object {
                -not $_.PSIsContainer -or $_.Name -notin @('bin', 'obj')
            }).Count -eq 0
            if (-not $isCodexBuild) {
                Write-Warning "Se conserva la carpeta con contenido no reconocido: $($child.FullName)"
                continue
            }
        }
        if ($isBuild -or $isCache -or $isCodexBuild) {
            $targets.Add($child.FullName)
            continue
        }

        # Tampoco inspeccionar dependencias, copias ni repositorios anidados.
        if ($child.Name -in @('.git', '.codex', 'artifacts', 'backups', 'NativeBuild',
                'node_modules', 'bin', 'obj', '.codex-updates', 'runtimes', 'native') -or
            $child.Name -like '*.backup' -or
            (Test-Path -LiteralPath (Join-Path $child.FullName '.git'))) { continue }
        $pending.Push($child.FullName)
    }
}

[long] $selectedBytes = 0
[long] $removedBytes = 0
$failures = 0
foreach ($target in ($targets | Sort-Object)) {
    try {
        Assert-LocalPath $target
        $relative = $target.Substring($rootPrefix.Length)
        # Una copia antigua recuperada no debe perder su unica clave por accidente.
        if ((Split-Path -Leaf $target) -eq '.artifacts' -and
            (Test-Path -LiteralPath (Join-Path $target 'update-signing/private.pem'))) {
            throw 'Hay una clave antigua recuperada en update-signing/private.pem. Trasladala a la ubicacion permanente indicada en doc/deployment/auto-updates.md antes de limpiar.'
        }
        $gitPrefix = $relative.Replace('\', '/') + '/'
        if (@($tracked | Where-Object {
            $_.StartsWith($gitPrefix, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0) {
            throw "Contiene archivos versionados: $relative"
        }
        $bytes = Get-LocalSize $target
        $selectedBytes += $bytes
        Write-Host ('{0,10:N2} MB  {1}' -f ($bytes / 1MB), $relative)
        if ($PSCmdlet.ShouldProcess($target, 'Eliminar carpeta regenerable')) {
            # Volver a validar justo antes de la eliminacion recursiva.
            Assert-LocalPath $target
            # Continuar con los archivos libres aunque otro este bloqueado.
            $removalErrors = @()
            Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue -ErrorVariable removalErrors
            $remainingBytes = if (Test-Path -LiteralPath $target) { Get-LocalSize $target } else { 0 }
            $removedBytes += [Math]::Max([long]0, [long]($bytes - $remainingBytes))
            if ($removalErrors.Count -gt 0) {
                throw "$($removalErrors.Count) errores de eliminacion. Primero: $($removalErrors[0].Exception.Message)"
            }
        }
    }
    catch {
        $failures++
        Write-Warning "No se ha podido limpiar ${target}: $($_.Exception.Message)"
    }
}

Write-Host ('Espacio seleccionado: {0:N2} MB. Eliminado: {1:N2} MB.' -f
    ($selectedBytes / 1MB), ($removedBytes / 1MB))
if ($WhatIfPreference) { Write-Host 'Vista previa: no se ha eliminado ningun archivo.' }
if ($failures -gt 0) {
    throw "No se pudieron limpiar $failures carpetas. Revisa los avisos anteriores."
}
