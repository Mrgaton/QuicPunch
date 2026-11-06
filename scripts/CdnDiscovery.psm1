Set-StrictMode -Version Latest
function Get-CdnMountRoots {
    $comparison=if($IsWindows){[StringComparer]::OrdinalIgnoreCase}else{[StringComparer]::Ordinal}
    $roots=[Collections.Generic.HashSet[string]]::new($comparison)
    if($IsWindows) {
        foreach($drive in Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue){[void]$roots.Add($drive.Root)}
    } else {
        if(Get-Command findmnt -ErrorAction SilentlyContinue) {
            function Add-Mount($items) {
                foreach($item in $items) {
                    if($item.target -notmatch '^/(proc|sys|dev)(/|$)'){[void]$roots.Add([string]$item.target)}
                    if($item.PSObject.Properties['children']){Add-Mount $item.children}
                }
            }
            $mounts=(& findmnt --json --real --output TARGET,FSTYPE 2>$null)|ConvertFrom-Json
            if($mounts){Add-Mount $mounts.filesystems}
        }
        foreach($base in @('/mnt','/media','/run/media')) {
            if(Test-Path -LiteralPath $base){[void]$roots.Add($base);foreach($child in Get-ChildItem -LiteralPath $base -Directory -ErrorAction SilentlyContinue){[void]$roots.Add($child.FullName)}}
        }
    }
    return @($roots)
}

function Resolve-CdnRoot {
    [CmdletBinding()]param([string]$ExplicitPath,[string[]]$Roots)
    if($ExplicitPath){
        $full=[IO.Path]::GetFullPath($ExplicitPath)
        if($full -eq [IO.Path]::GetPathRoot($full) -or !(Test-Path -LiteralPath $full -PathType Container)){throw 'CdnRoot debe ser una carpeta existente, no la raíz de un disco.'}
        return $full
    }
    if(!$Roots){$Roots=Get-CdnMountRoots}
    $comparison=if($IsWindows){[StringComparer]::OrdinalIgnoreCase}else{[StringComparer]::Ordinal}
    $found=[Collections.Generic.HashSet[string]]::new($comparison)
    foreach($root in $Roots){
        if(!(Test-Path -LiteralPath $root -PathType Container)){continue}
        $bases=@($root)
        # Bounded scan: mount root and one level, never a recursive crawl of the disks.
        $bases+=@(Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue|Where-Object Name -NotMatch '^(Windows|Program Files.*|Users|proc|sys|dev|\.git)$'|Select-Object -First 100 -ExpandProperty FullName)
        foreach($base in $bases){
            foreach($name in @('QuickPunch','QuicPunch','QuicNet')){
                $candidate=Join-Path $base "CDN/$name"
                if(Test-Path -LiteralPath $candidate -PathType Container){[void]$found.Add([IO.Path]::GetFullPath($candidate))}
                if((Split-Path $base -Leaf) -ieq 'CDN'){
                    $candidate=Join-Path $base $name
                    if(Test-Path -LiteralPath $candidate -PathType Container){[void]$found.Add([IO.Path]::GetFullPath($candidate))}
                }
            }
        }
    }
    # Windows paths differing only in letter case represent the same directory.
    $candidates=@($found|Sort-Object -CaseSensitive)
    if($candidates.Count -eq 1){return $candidates[0]}
    if(!$candidates.Count){throw 'No se encontró CDN/QuickPunch ni CDN/QuicNet en los discos montados. Monta el disco o indica -CdnRoot.'}
    throw "Se encontraron varios CDN: $($candidates -join ', '). Indica -CdnRoot para elegir."
}
Export-ModuleMember -Function Resolve-CdnRoot,Get-CdnMountRoots
