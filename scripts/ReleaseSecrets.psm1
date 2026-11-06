# Persistent release identities must survive deletion of every build directory.
function Resolve-PersistentReleaseSecretPath {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $RepositoryRoot,
        [string] $CdnRoot
    )

    $fullPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    foreach ($forbiddenRoot in @($RepositoryRoot, $CdnRoot)) {
        if (-not $forbiddenRoot) { continue }
        $forbidden = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($forbiddenRoot)
        $prefix = $forbidden.TrimEnd([char[]]@('/', '\')) + [IO.Path]::DirectorySeparatorChar
        if ($fullPath.Equals($forbidden, $comparison) -or $fullPath.StartsWith($prefix, $comparison)) {
            throw 'Las claves privadas y keystores deben guardarse fuera del repositorio y del CDN.'
        }
    }
    if (($fullPath -split '[/\\]') -icontains '.artifacts' -or
        ($fullPath -split '[/\\]') -icontains 'artifacts') {
        throw 'No se permiten claves privadas ni keystores en carpetas artifacts o .artifacts.'
    }
    # Do not allow a link to disguise a disposable directory as a persistent one.
    $current = $fullPath
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "La ruta de la clave no puede atravesar enlaces: $current"
            }
        }
        $current = Split-Path -Parent $current
    }
    return $fullPath
}

function Get-DefaultReleaseSigningKeyPath {
    $dataRoot = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData,
        [Environment+SpecialFolderOption]::DoNotVerify)
    if (-not $dataRoot) { throw 'No se encuentra la carpeta de datos del usuario. Especifica -SigningKey.' }
    return Join-Path $dataRoot 'QuicNet/signing/release-private.pem'
}

function Get-DefaultAndroidSigningKeyStorePath {
    $dataRoot = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData,
        [Environment+SpecialFolderOption]::DoNotVerify)
    if (-not $dataRoot) { throw 'No se encuentra la carpeta de datos del usuario. Especifica -AndroidKeyStore.' }
    return Join-Path $dataRoot 'QuicNet/signing/android-release.keystore'
}

Export-ModuleMember -Function Resolve-PersistentReleaseSecretPath, Get-DefaultReleaseSigningKeyPath, Get-DefaultAndroidSigningKeyStorePath
