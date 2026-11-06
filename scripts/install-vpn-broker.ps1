param(
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$OwnerSid
)
$ErrorActionPreference = 'Stop'
$installIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]::new($installIdentity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer once as Administrator.'
}
$null = [Security.Principal.SecurityIdentifier]::new($OwnerSid)
$publishRoot = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'QuicNet.exe'))) { throw 'Choose a published QuicNet directory containing QuicNet.exe.' }
$brokerDirectory = Join-Path $env:ProgramFiles 'QuicNet.Vpn'
$policyDirectory = Join-Path $env:ProgramData 'QuicNet'
$policyFile = Join-Path $policyDirectory 'vpn-broker.json'
if ($publishRoot.TrimEnd('\').Equals($brokerDirectory, [StringComparison]::OrdinalIgnoreCase) -or
    $publishRoot.StartsWith($brokerDirectory + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $brokerDirectory.StartsWith($publishRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The publish directory and service directory must be separate.'
}
function Get-SafeEntries([string]$root) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($root)
    while ($pending.Count -gt 0) {
        $entryPath = $pending.Dequeue()
        $entry = Get-Item -LiteralPath $entryPath -Force
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Symbolic links and junctions are not allowed: $entryPath" }
        $entry
        if ($entry.PSIsContainer) { Get-ChildItem -LiteralPath $entryPath -Force | ForEach-Object { $pending.Enqueue($_.FullName) } }
    }
}
# Verify every entry before copying files or changing the running service.
$null = @(Get-SafeEntries $publishRoot)
$null = @(Get-SafeEntries $brokerDirectory)
$null = @(Get-SafeEntries $policyDirectory)
if (Test-Path -LiteralPath $policyFile) {
    if ((Get-Content -LiteralPath $policyFile -Raw | ConvertFrom-Json).owner -ne $OwnerSid) {
        throw 'The existing service belongs to a different user. Set owner in the administrator policy before reinstalling.'
    }
}
function Protect-ServiceEntry($entry) {
    if ($entry.PSIsContainer) { $acl = [Security.AccessControl.DirectorySecurity]::new(); $inherit = 'ContainerInherit, ObjectInherit' }
    else { $acl = [Security.AccessControl.FileSecurity]::new(); $inherit = 'None' }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
    foreach ($rule in @(@('S-1-5-18', 'FullControl'), @('S-1-5-32-544', 'FullControl'), @('S-1-5-32-545', 'ReadAndExecute'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new(
            [Security.Principal.SecurityIdentifier]::new($rule[0]), $rule[1], $inherit, 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $entry.FullName -AclObject $acl
}
$existingService = Get-Service -Name 'QuicNetVpnBroker' -ErrorAction SilentlyContinue
if ($existingService) { Stop-Service -Name 'QuicNetVpnBroker'; $existingService.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
foreach ($directory in @($brokerDirectory, $policyDirectory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Get-SafeEntries $directory | ForEach-Object { Protect-ServiceEntry $_ }
}
# Enumerate entries without constructing shell commands.
Get-ChildItem -LiteralPath $publishRoot | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $brokerDirectory -Recurse -Force }
if (-not (Test-Path -LiteralPath $policyFile)) {
    @{ owner = $OwnerSid; adapterName = 'QuicPunchAdapter'; allowedAddresses = @('10.0.0.0/24');
        allowedRoutes = @('10.0.0.0/24'); minimumPrefixLength = 24 } | ConvertTo-Json | Set-Content -LiteralPath $policyFile -Encoding UTF8
}
Get-SafeEntries $brokerDirectory | ForEach-Object { Protect-ServiceEntry $_ }
Get-SafeEntries $policyDirectory | ForEach-Object { Protect-ServiceEntry $_ }
$brokerCommand = '"' + (Join-Path $brokerDirectory 'QuicNet.exe') + '" --vpn-broker-service "' + $policyFile + '"'
if ($existingService) { & sc.exe config QuicNetVpnBroker binPath= $brokerCommand start= auto obj= LocalSystem | Out-Null }
else { & sc.exe create QuicNetVpnBroker binPath= $brokerCommand start= auto obj= LocalSystem DisplayName= 'QuicNet VPN adapter' | Out-Null }
if ($LASTEXITCODE -ne 0) { throw 'Could not register the VPN service.' }
& sc.exe failure QuicNetVpnBroker reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
Start-Service -Name 'QuicNetVpnBroker'
Write-Host "VPN service installed. Administrator policy: $policyFile"
Write-Host 'The desktop application can now run without elevation.'
