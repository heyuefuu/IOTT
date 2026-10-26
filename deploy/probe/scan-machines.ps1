#Requires -Version 5.1
<#
  scan-machines.ps1 - run ON THE TARGET MACHINE (or any PC on the machine-tool LAN).
  Finds every live host on the local industrial subnets, scans industrial ports and
  fingerprints protocols, then writes CSV/JSON/Markdown to .\out\ for bringing back.

  Read-only: Modbus FC03, S7 COTP CR, OPC UA HEL, MTConnect GET /probe, FTP banner,
  MQTT CONNECT, FOCAS probe (optional), HTTP GET only. No writes to any device.

  Examples:
    .\scan-machines.ps1
    .\scan-machines.ps1 -Cidrs 192.168.0.0/24,192.168.1.0/24
    .\scan-machines.ps1 -IncludeWlan -TimeoutMs 600
    .\scan-machines.ps1 -WatchSec 60            # keep watching while machines are plugged in
    .\scan-machines.ps1 -FocasProbePath C:\iott-bundle\host\FocasProbe.exe
#>
[CmdletBinding()]
param(
    [string[]]$Cidrs,
    [int[]]$Ports = @(21, 22, 23, 80, 102, 135, 139, 443, 445, 502, 1883, 2049, 3306, 3307,
        3389, 4840, 5000, 5007, 5087, 5173, 5900, 5985, 5986, 8080, 8082, 8090, 8181, 8193,
        9600, 9999, 11520, 18083, 19001, 27017, 60008),
    [int]$TimeoutMs = 450,
    [int]$Parallel = 128,
    [int]$MaxHostsPerCidr = 512,
    [string]$OutDir,
    [string]$FocasProbePath = '',
    [switch]$IncludeWlan,
    [switch]$SkipPing,
    [switch]$ScanAllHosts,
    [int]$WatchSec = 0
)

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'ProbeKit.ps1')

if (-not $OutDir) { $OutDir = Join-Path $PSScriptRoot 'out' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Info($t) { Write-Host "  [scan] $t" -ForegroundColor Gray }
function Ok($t) { Write-Host "  [scan] $t" -ForegroundColor Green }
function Warn($t) { Write-Host "  [scan] $t" -ForegroundColor Yellow }

function Invoke-ScanRound {
    param([string]$Tag, [System.Collections.Generic.HashSet[string]]$SeenHosts)

    $roundStart = Get-Date
    Write-Host ''
    Write-Host "===== scan round $Tag  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') =====" -ForegroundColor Cyan

    # 1) subnets
    $cidrs = @()
    if ($Cidrs -and $Cidrs.Count -gt 0) { $cidrs = $Cidrs }
    else {
        $cidrs = @(Get-LocalCidrs -IncludeWlan:$IncludeWlan)
        if (-not $cidrs -or $cidrs.Count -eq 0) {
            Warn 'no candidate subnet detected; please pass -Cidrs 192.168.0.0/24'
            return
        }
    }
    Info "subnets: $($cidrs -join ', ')"

    # interfaces (context)
    Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        ForEach-Object { Info "local ip: $($_.IPAddress)/$($_.PrefixLength)" }

    # 2) expand
    $ips = New-Object System.Collections.Generic.List[string]
    foreach ($c in $cidrs) {
        foreach ($ip in (Expand-Cidr -Cidr $c -MaxHosts $MaxHostsPerCidr)) { if (-not $ips.Contains($ip)) { $ips.Add($ip) | Out-Null } }
    }
    $own = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -ExpandProperty IPAddress)
    $ips = @($ips | Where-Object { $own -notcontains $_ })
    if ($ips.Count -eq 0) { Warn 'empty host list'; return }
    Info "hosts to probe: $($ips.Count)  ports: $($Ports.Count)  timeout: ${TimeoutMs}ms  parallel: $Parallel"

    # 3) L2 presence: one UDP datagram per host triggers ARP, then read the neighbor table.
    #    This works even when ICMP/TCP are filtered (SendARP is unreliable on bridge NICs).
    $live = @{}
        $ipSet = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($ip in $ips) { [void]$ipSet.Add($ip) }
    $swp = [Diagnostics.Stopwatch]::StartNew()
    try { [void][IottProbe.Scan]::UdpPoke([string[]]$ips, 9) } catch { }
    Start-Sleep -Milliseconds 1500
    foreach ($n in (Get-ArpTable)) {
        if ($ipSet.Contains($n.IP) -and $n.MAC -and $n.MAC -ne '00-00-00-00-00-00') { $live[$n.IP] = $n.MAC }
    }
    $swp.Stop()
    Info "arp sweep (udp poke): $($live.Count) live host(s) in $([int]$swp.Elapsed.TotalSeconds)s"

    # 4) ping sweep (adds routed hosts; optional)
    $pingUp = @()
    if (-not $SkipPing) {
        $swp.Restart()
        try { $pingUp = @([IottProbe.Scan]::Ping([string[]]$ips, [Math]::Max(250, $TimeoutMs), [Math]::Min($Parallel, 128))) } catch { }
        $swp.Stop()
        Info "ping sweep: $($pingUp.Count) reply in $([int]$swp.Elapsed.TotalSeconds)s"
        foreach ($ip in $pingUp) { if (-not $live.ContainsKey($ip)) { $live[$ip] = '' } }
    }

    if ($live.Count -eq 0 -and -not $ScanAllHosts) {
        Warn 'no live host found (ARP + ICMP). Wrong NIC/subnet? Use -Cidrs or -ScanAllHosts.'
        return
    }

    $targets = if ($ScanAllHosts) { [string[]]$ips } else { [string[]]@($live.Keys) }
    Info "port scan targets: $($targets.Count) host(s) x $($Ports.Count) port(s)"

    # 5) TCP scan (live hosts only)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $entries = @()
    try { $entries = @([IottProbe.Scan]::TcpMany([string[]]$targets, [int[]]$Ports, $TimeoutMs, $Parallel)) } catch { $entries = @() }
    $sw.Stop()
    Info "tcp scan done in $([int]$sw.Elapsed.TotalSeconds)s; open entries: $($entries.Count)"

    $openMap = @{}
    foreach ($e in $entries) {
        $parts = $e.Split('|')
        if ($parts.Length -ne 2) { continue }
        $hh = $parts[0]; $pp = [int]$parts[1]
        if (-not $openMap.ContainsKey($hh)) { $openMap[$hh] = New-Object System.Collections.Generic.List[int] }
        $openMap[$hh].Add($pp)
    }

    # 6) fingerprint
    $results = New-Object System.Collections.Generic.List[object]
    $hosts = @($openMap.Keys | Sort-Object { [version]($_ -replace '-', '.') } )
    foreach ($hh in $hosts) {
        $ports = @($openMap[$hh] | Sort-Object -Unique)
        Info "probing $hh ($($ports -join ', '))"
        foreach ($pp in $ports) {
            $info = $null
            try { $info = Probe-Any -TargetHost $hh -TargetPort $pp -TimeoutMs ([Math]::Max(1000, $TimeoutMs)) -FocasProbePath $FocasProbePath } catch { }
            if (-not $info) { $info = New-ProbeResult $hh $pp 'TCP(未识别)' 'port open, no signature' '' 'Unknown' }
            $results.Add($info) | Out-Null
        }
    }

    # 7) console summary
    Write-Host ''
    Write-Host '----- hosts -----' -ForegroundColor Cyan
    foreach ($hh in $hosts) {
        $mac = if ($live.ContainsKey($hh)) { $live[$hh] } else { '' }
        $protos = @($results | Where-Object { $_.Host -eq $hh } | ForEach-Object { $_.Protocol } | Sort-Object -Unique)
        Ok ("{0,-16} mac={1,-18} {2}" -f $hh, $mac, ($protos -join ', '))
        foreach ($r in ($results | Where-Object { $_.Host -eq $hh })) {
            Write-Host ("    {0,-6} {1,-26} {2}" -f $r.Port, $r.Protocol, $r.Evidence) -ForegroundColor DarkGray
        }
    }
    $quiet = @($live.Keys | Where-Object { -not $openMap.ContainsKey($_) } | Sort-Object)
    if ($quiet.Count -gt 0) {
        Warn "present at L2 but no scanned port open (ICMP/TCP filtered?): $(($quiet | ForEach-Object { "$($_.IP)($($_.MAC))" }) -join ', ')"
    }

    Write-Host ''
    Write-Host '----- suggested device config -----' -ForegroundColor Cyan
    $devices = @($results | Where-Object { $_.Kind -eq 'Device' -or $_.Kind -eq 'Transfer' })
    if ($devices.Count -eq 0) { Warn 'no device protocol identified in this round' }
    foreach ($r in $devices) {
        Write-Host ("  [{0}] {1}" -f $r.Host, (Get-SuggestedConfig $r)) -ForegroundColor Green
    }

    # 8) new hosts (watch mode)
    if ($SeenHosts) {
        foreach ($hh in $hosts) { if ($SeenHosts.Add($hh)) { Write-Host "  [NEW] $hh appeared" -ForegroundColor Magenta } }
    }

    # 9) report files
    $hostRows = foreach ($hh in $hosts) {
        $mac = if ($live.ContainsKey($hh)) { $live[$hh] } else { '' }
        foreach ($r in ($results | Where-Object { $_.Host -eq $hh })) {
            [pscustomobject]@{
                Host = $hh; MAC = $mac; Port = $r.Port; Protocol = $r.Protocol; Kind = $r.Kind
                Evidence = $r.Evidence; Hint = $r.Hint; SuggestedConfig = (Get-SuggestedConfig $r)
            }
        }
    }
    $hostRows = @($hostRows)
    $csv = Join-Path $OutDir "scan-$Tag.csv"
    $json = Join-Path $OutDir "scan-$Tag.json"
    $md = Join-Path $OutDir "scan-$Tag.md"
    $hostRows | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
    [pscustomobject]@{
        generatedAt = (Get-Date -Format o)
        probeHost = $env:COMPUTERNAME
        cidrs = $cidrs
        hostsScanned = $ips.Count
        pingReplies = $pingUp.Count
        liveHosts = $live.Count
        openEntries = $entries.Count
        durationSeconds = [int]((Get-Date) - $roundStart).TotalSeconds
        live = @($live.Keys | Sort-Object | ForEach-Object { [pscustomobject]@{ ip = $_; mac = $live[$_] } })
        results = $hostRows
    } | ConvertTo-Json -Depth 6 | Set-Content -Path $json -Encoding UTF8

    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# Machine scan $Tag")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine("- time: $(Get-Date -Format o)")
    [void]$sb.AppendLine("- probe host: $env:COMPUTERNAME")
    [void]$sb.AppendLine("- subnets: $($cidrs -join ', ')")
    [void]$sb.AppendLine("- hosts scanned: $($ips.Count), ping replies: $($pingUp.Count), live hosts: $($live.Count)")
    [void]$sb.AppendLine('')
    [void]$sb.AppendLine('| host | mac | port | protocol | evidence | suggested config |')
    [void]$sb.AppendLine('|---|---|---|---|---|---|')
    foreach ($r in $hostRows) {
        [void]$sb.AppendLine("| $($r.Host) | $($r.MAC) | $($r.Port) | $($r.Protocol) | $($r.Evidence -replace '\|','/') | $($r.SuggestedConfig -replace '\|','/') |")
    }
    Set-Content -Path $md -Value $sb.ToString() -Encoding UTF8
    Copy-Item $csv (Join-Path $OutDir 'latest.csv') -Force
    Copy-Item $json (Join-Path $OutDir 'latest.json') -Force
    Copy-Item $md (Join-Path $OutDir 'latest.md') -Force
    Ok "report: $csv"
    Ok "report: $json"
    Ok "report: $md"
}

$seen = New-Object System.Collections.Generic.HashSet[string]
$tag = Get-Date -Format 'yyyyMMdd-HHmmss'
Invoke-ScanRound -Tag $tag -SeenHosts $seen
if ($WatchSec -gt 0) {
    Info "watch mode: rescan every ${WatchSec}s (Ctrl+C to stop)"
    while ($true) {
        Start-Sleep -Seconds $WatchSec
        $tag = Get-Date -Format 'yyyyMMdd-HHmmss'
        try { Invoke-ScanRound -Tag $tag -SeenHosts $seen } catch { Warn "round failed: $($_.Exception.Message)" }
    }
}
