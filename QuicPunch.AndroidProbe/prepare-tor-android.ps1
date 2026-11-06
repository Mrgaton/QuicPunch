$ErrorActionPreference = 'Stop'

# Guardian Project's Android build of the same pinned Tor version as desktop.
$version = '0.4.9.11'
$sha256 = '018a0b0bc28f83364595cdc729fc1e2a559d408bf91b9f4cef0f7f9b907c73a4'
$uri = "https://raw.githubusercontent.com/guardianproject/gpmaven/master/info/guardianproject/tor-android/$version/tor-android-$version.aar"
$archive = Join-Path ([IO.Path]::GetTempPath()) ("quicpunch-tor-" + [guid]::NewGuid().ToString('N') + '.aar')
$destination = Join-Path $PSScriptRoot 'native/arm64-v8a/libtor.so'

try {
    Invoke-WebRequest -Uri $uri -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) {
        throw 'Tor Android archive SHA-256 verification failed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $entry = $zip.GetEntry('jni/arm64-v8a/libtor.so')
        if ($null -eq $entry) { throw 'Tor Android archive has no ARM64 executable.' }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $destination, $true)
    }
    finally { $zip.Dispose() }
    Write-Host "Tor $version for Android ARM64 prepared at $destination"
}
finally {
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
}
