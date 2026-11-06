param([string]$Cache = (Join-Path $PSScriptRoot '../obj/catalog-source'))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $Cache -Force | Out-Null
$all = [Collections.Generic.List[object]]::new()
$hashes = [Collections.Generic.List[object]]::new()
$next = 'https://atlas.ripe.net/api/v2/probes/?is_public=true&status=1&page_size=500'
$page = 0
while ($next) {
    $page++
    $file = Join-Path $Cache "page-$page.json"
    if (-not (Test-Path -LiteralPath $file)) { Invoke-WebRequest -Uri $next -OutFile $file }
    $json = [IO.File]::ReadAllText($file) | ConvertFrom-Json
    $hashes.Add([ordered]@{ url = $next; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash })
    foreach ($probe in $json.results) { $all.Add($probe) }
    $next = $json.next
    if ($page % 5 -eq 0) { Write-Output "Read $page pages / $($all.Count) public metadata records" }
    if ($page -gt 100) { throw 'Unexpected pagination' }
}
$records = [Collections.Generic.List[object]]::new()
$addresses = [Collections.Generic.HashSet[string]]::new()
foreach ($probe in ($all | Sort-Object id)) {
    $tags = @($probe.tags | ForEach-Object slug)
    if (-not $probe.is_public -or $probe.status.id -ne 1 -or $probe.is_anchor -or
        $tags -contains 'system-geoloc-disputed' -or $tags -contains 'system-on-hold' -or
        ($tags | Where-Object { $_ -match '(anycast|vpn|tunnel)' }) -or $probe.geometry.type -ne 'Point') { continue }
    $coords = $probe.geometry.coordinates
    if ($coords.Count -ne 2 -or [Math]::Abs($coords[0]) -gt 180 -or [Math]::Abs($coords[1]) -gt 90) { continue }
    foreach ($family in 4,6) {
        if ($tags -notcontains "system-ipv$family-works" -or $tags -notcontains "system-ipv$family-stable-30d") { continue }
        if ($family -eq 4 -and ($tags -contains 'system-ipv4-rfc1918' -or $tags -contains 'nat')) { continue }
        $ip = $null
        if (-not [Net.IPAddress]::TryParse($probe."address_v$family", [ref]$ip)) { continue }
        $b = $ip.GetAddressBytes()
        if ($family -eq 4) {
            if ($b.Length -ne 4 -or $b[0] -in 0,10,127 -or $b[0] -ge 224 -or
                ($b[0] -eq 100 -and $b[1] -ge 64 -and $b[1] -le 127) -or
                ($b[0] -eq 169 -and $b[1] -eq 254) -or
                ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or
                ($b[0] -eq 192 -and $b[1] -eq 168)) { continue }
        } elseif ($b.Length -ne 16 -or ($b[0] -band 224) -ne 32) { continue }
        if (-not $addresses.Add($ip.ToString())) { continue }
        $records.Add([ordered]@{
            id = "atlas-probe-$($probe.id)-v$family"; address = $ip.ToString()
            position = [ordered]@{ latitude = [double]$coords[1]; longitude = [double]$coords[0] }
            source = "RIPE Atlas public probe metadata; snapshot 2026-10-18; https://atlas.ripe.net/probes/$($probe.id)/"
            siteId = "atlas-probe-$($probe.id)"; asn = $probe."asn_v$family"
            name = "Atlas probe $($probe.id) ($($probe.country_code))"; countryCode = $probe.country_code
            kind = 'anchor'; locationVerified = $false; anycast = $false; uncertaintyKm = 50
            coordinateEvidence = 'operator-declared'
        })
    }
}
if ($records.Count -lt 1000 -or $records.Count -gt 17000) { throw "Unexpected supplemental catalogue size: $($records.Count)" }
$data = Join-Path $PSScriptRoot '../Data'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$destination = Join-Path $data 'regional-probes.json.gz'
$stream = [IO.File]::Create($destination)
$gzip = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionLevel]::SmallestSize)
try {
    $bytes = [Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $records.ToArray() -Depth 5 -Compress))
    $gzip.Write($bytes,0,$bytes.Length)
} finally { $gzip.Dispose(); $stream.Dispose() }
$manifest = [ordered]@{
    snapshot = '2026-10-18'; fetchedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    sourceRecords = $all.Count; endpoints = $records.Count
    sites = @($records | ForEach-Object { $_.siteId } | Sort-Object -Unique).Count
    ipv4 = @($records | Where-Object { $_.id -like '*-v4' }).Count
    ipv6 = @($records | Where-Object { $_.id -like '*-v6' }).Count
    countries = @($records | ForEach-Object { $_.countryCode } | Sort-Object -Unique).Count
    spainEndpoints = @($records | Where-Object { $_.countryCode -eq 'ES' }).Count
    sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
    policy = 'Public, connected, non-anchor, works + stable-30d; exclude disputed/on-hold, declared VPN/tunnel/anycast and IPv4 NAT. IP deduplicated. No physical verification: separate conditional bounds only, nominal 50 km metadata uncertainty is not calibrated.'
    sources = $hashes.ToArray()
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $data 'regional-manifest.json') -Encoding utf8
[pscustomobject]$manifest | Select-Object endpoints,sites,ipv4,ipv6,countries,spainEndpoints,sha256 | Format-List
