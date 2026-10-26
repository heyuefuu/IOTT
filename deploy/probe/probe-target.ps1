#Requires -Version 5.1
<#
  probe-target.ps1 - probe the target machine (IOTT host / gateway) from this PC.
  Read-only. Tries every available channel and explains what to do next.

  Examples:
    .\probe-target.ps1
    .\probe-target.ps1 -Target 192.168.1.201
    .\probe-target.ps1 -Target 192.168.1.200 -Listen        # also start reverse listener on 4444
    .\probe-target.ps1 -Json

  Exit code: 0 = at least one TCP port reachable; 2 = no TCP port reachable (L2/firewall).
#>
[CmdletBinding()]
param(
    [string]$Target = '192.168.1.200',
    [int[]]$Ports = @(21, 22, 23, 80, 102, 135, 139, 443, 445, 502, 1883, 2049, 3306, 3307, 3389,
        4840, 5000, 5007, 5087, 5173, 5900, 5985, 5986, 8080, 8082, 8090, 8181, 8193, 9600,
        9999, 11520, 18083, 19001, 27017, 60008),
    [int]$TimeoutMs = 700,
    [int]$Parallel = 128,
    [string]$OutDir = '',
    [string]$FocasProbePath = '',
    [string]$SshUser = 'ktb',
    [switch]$Listen,
    [int]$ListenPort = 4444,
    [switch]$Json
)

$ErrorActionPreference = 'Continue'
if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
. (Join-Path $PSScriptRoot 'ProbeKit.ps1')

function Sec($t) { Write-Host ''; Write-Host "===== $t =====" -ForegroundColor Cyan }
function Ok($t) { Write-Host "  [OK]   $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [WARN] $t" -ForegroundColor Yellow }
function Bad($t) { Write-Host "  [FAIL] $t" -ForegroundColor Red }
function Info($t) { Write-Host "  [INFO] $t" -ForegroundColor Gray }

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$openPorts = @()
$results = @()
$probeStart = Get-Date

Write-Host "IOTT target probe  target=$Target  time=$(Get-Date -Format o)" -ForegroundColor White
Write-Host "ports=$($Ports.Count) timeout=${TimeoutMs}ms parallel=$Parallel  run from=$env:COMPUTERNAME" -ForegroundColor DarkGray

# ---------------------------------------------------------------- 1. local side
Sec '1. local network state'
$localIps = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
    Select-Object -ExpandProperty IPAddress
foreach ($ip in $localIps) { Info "local ip: $ip" }

$route = Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.DestinationPrefix -eq '0.0.0.0/0' -or ($Target -match '^(\d+\.\d+\.\d+)\.' -and $_.DestinationPrefix -like "$($Matches[1]).*") } |
    Sort-Object { $_.RouteMetric + $_.InterfaceMetric }
if ($route) {
    $r0 = $route | Select-Object -First 1
    Info "route: $($r0.DestinationPrefix) via $($r0.NextHop) if=$($r0.InterfaceAlias) metric=$($r0.RouteMetric + $r0.InterfaceMetric)"
}

$arp = Get-ArpTable | Where-Object { $_.IP -eq $Target } | Select-Object -First 1
if ($arp) {
    Ok "ARP: $($arp.IP) -> $($arp.MAC) state=$($arp.State) if=$($arp.Iface)"
    Info "L2 is reachable (MAC learned). ICMP/TCP may still be filtered by the target."
} else {
    Warn "no ARP entry for $Target (target may be on another L3 segment or not yet contacted)"
}

# ---------------------------------------------------------------- 2. ICMP
Sec '2. ICMP'
$icmpOk = $false
for ($i = 1; $i -le 3; $i++) {
    $p = $false
    try { $p = [IottProbe.Scan]::PingOne($Target, [Math]::Max(600, $TimeoutMs)) } catch { }
    if ($p) { $icmpOk = $true; Ok "ping #${i}: reply" } else { Warn "ping #${i}: no reply" }
}
if (-not $icmpOk) { Info 'ICMP is blocked or filtered. TCP scan below decides reachability.' }

# ---------------------------------------------------------------- 3. TCP scan
Sec "3. TCP scan ($($Ports.Count) ports)"
$sw = [Diagnostics.Stopwatch]::StartNew()
try { $openPorts = @([IottProbe.Scan]::Tcp($Target, $Ports, $TimeoutMs, $Parallel)) } catch { $openPorts = @() }
$sw.Stop()
if ($openPorts.Count -gt 0) {
    Ok "$($openPorts.Count) open port(s) in $([int]$sw.Elapsed.TotalSeconds)s: $($openPorts -join ', ')"
} else {
    Bad "no reachable TCP port in $([int]$sw.Elapsed.TotalSeconds)s"
}

# ---------------------------------------------------------------- 4. fingerprint
if ($openPorts.Count -gt 0) {
    Sec '4. service fingerprint'
    foreach ($p in $openPorts) {
        $info = $null
        try { $info = Probe-Any -TargetHost $Target -TargetPort ([int]$p) -TimeoutMs ([Math]::Max(1200, $TimeoutMs)) -FocasProbePath $FocasProbePath } catch { }
        if (-not $info) {
            $info = New-ProbeResult $Target ([int]$p) 'TCP(未识别)' 'port open, no signature' '' 'Unknown'
        }
        $results += $info
        $line = "{0,-6} {1,-24} {2}" -f $info.Port, $info.Protocol, $info.Evidence
        if ($info.Protocol -like 'TCP*') { Warn $line } else { Ok $line }
    }
}

# ---------------------------------------------------------------- 5. verdict
Sec '5. verdict / next steps'
$byProto = @{}
foreach ($r in $results) { $byProto[$r.Protocol] = $r }

$hasSsh = $results | Where-Object { $_.Protocol -eq 'SSH' }
$hasSmb = $results | Where-Object { $_.Protocol -like 'SMB*' }
$hasWinrm = $results | Where-Object { $_.Port -in @(5985, 5986) }
$hasRdp = $results | Where-Object { $_.Protocol -eq 'RDP' }
$hasHost = $results | Where-Object { $_.Protocol -eq 'IOTT Host' }
$hasGw = $results | Where-Object { $_.Protocol -eq 'IOTT 网关' }
$hasWeb = $results | Where-Object { $_.Port -eq 8082 }

if ($hasSsh) {
    $key = Join-Path $env:USERPROFILE '.ssh\id_ed25519_obugs'
    if (Test-Path $key) {
        $sshTry = & ssh -i $key -o BatchMode=yes -o ConnectTimeout=6 -o StrictHostKeyChecking=accept-new "$SshUser@$Target" 'hostname' 2>&1
        if ($LASTEXITCODE -eq 0) { Ok "SSH key login works: $sshTry"; Info "use: ssh -i `"$key`" ktb@$Target" }
        else { Warn "SSH port is open but key login failed: $($sshTry -join ' ')" }
    } else {
        Info "SSH open. Run: ssh $SshUser@$Target"
    }
}
if ($hasWinrm) { Info "WinRM open. Try: Test-WSMan $Target  (then New-PSSession -ComputerName $Target -Credential <user>)" }
if ($hasSmb) { Info "TCP 445 open. Try: net use \\$Target\C$ /user:<user> <password>   /   Get-SmbShare -CimSession $Target" }
if ($hasRdp) { Info "RDP open. Use mstsc /v:$Target" }
if ($hasHost -or $hasGw -or $hasWeb) {
    Info "IOTT services are reachable:"
    if ($hasHost) { Info "  Host API   http://${Target}:$($hasHost.Port)/api/Devices" }
    if ($hasGw) { Info "  Gateway    http://${Target}:$($hasGw.Port)/api/health" }
    if ($hasWeb) { Info "  Web        http://${Target}:8082/" }
}

if ($openPorts.Count -eq 0) {
    $subnetPrefix = if ($Target -match '^(\d+\.\d+\.\d+)\.') { "$($Matches[1])." } else { '' }
    $bindIp = '192.168.1.50'
    if ($subnetPrefix) {
        $cand = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -like "$subnetPrefix*" } | Select-Object -First 1
        if ($cand) { $bindIp = $cand.IPAddress }
    }
    Warn 'No TCP channel from this PC. ARP proves L2 presence, so the target firewall is dropping this PC.'
    Info 'Options (in order of speed):'
    Info '  A. On the target (console/any existing session) run reverse-agent.ps1:'
    Info "       powershell -NoProfile -ExecutionPolicy Bypass -File .\reverse-agent.ps1 -Server $bindIp -Port $ListenPort"
    Info '     then on this PC:'
    Info "       powershell -NoProfile -ExecutionPolicy Bypass -File .\listener.ps1 -Port $ListenPort"
    Info '  B. Use the I: drive: copy this probe kit to I:\probe, then run scan-machines.ps1 on the target and bring out\ back.'
    Info '  C. On the target, allow inbound (admin):'
    Info '       netsh advfirewall firewall add rule name="IOTT-PROBE" dir=in action=allow protocol=TCP localport=22,445,5173,5087,8082 profile=any'
    Info '       netsh advfirewall firewall add rule name="IOTT-ICMP" dir=in action=allow protocol=icmpv4 profile=any'
}

# ---------------------------------------------------------------- 6. report
try { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null } catch { }
$report = [pscustomobject]@{
    generatedAt = (Get-Date -Format o)
    target = $Target
    probeHost = $env:COMPUTERNAME
    icmp = $icmpOk
    arp = $arp
    openPorts = $openPorts
    results = $results
    durationMs = [int]((Get-Date) - $probeStart).TotalMilliseconds
}
$jsonPath = Join-Path $OutDir "probe-target-$stamp.json"
$mdPath = Join-Path $OutDir "probe-target-$stamp.md"
$report | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonPath -Encoding UTF8

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Target probe: $Target")
[void]$sb.AppendLine('')
[void]$sb.AppendLine("- time: $($report.generatedAt)")
[void]$sb.AppendLine("- from: $($report.probeHost)")
[void]$sb.AppendLine("- ICMP: $icmpOk")
[void]$sb.AppendLine("- ARP: $($arp.IP) $($arp.MAC) $($arp.State)")
[void]$sb.AppendLine("- open ports: $($openPorts -join ', ')")
[void]$sb.AppendLine('')
[void]$sb.AppendLine('| port | protocol | evidence | suggestion |')
[void]$sb.AppendLine('|---|---|---|---|')
foreach ($r in $results) {
    [void]$sb.AppendLine("| $($r.Port) | $($r.Protocol) | $($r.Evidence -replace '\|','/') | $((Get-SuggestedConfig $r) -replace '\|','/') |")
}
Set-Content -Path $mdPath -Value $sb.ToString() -Encoding UTF8
Ok "report: $jsonPath"
Ok "report: $mdPath"

if ($Json) { $report | ConvertTo-Json -Depth 6 }

if ($Listen) {
    Info "starting reverse listener on port $ListenPort ..."
    & (Join-Path $PSScriptRoot 'listener.ps1') -Port $ListenPort
}

if ($openPorts.Count -eq 0) { exit 2 } else { exit 0 }
