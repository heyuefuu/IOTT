#Requires -Version 5.1
<#
  reverse-agent.ps1 - run ON THE TARGET MACHINE when the probe PC cannot reach it
  (only target -> probe PC direction works).

  It connects back to the probe PC and serves a FIXED whitelist of read-only
  diagnostics. There is no arbitrary command execution.

  Usage (on the target):
    powershell -NoProfile -ExecutionPolicy Bypass -File .\reverse-agent.ps1 -Server 192.168.1.50 -Port 4444

  Commands served: info ipconfig route arp neighbor listening netstat firewall
                   services disk hosts time scan quit
#>
[CmdletBinding()]
param(
    [string]$Server = '192.168.1.50',
    [int]$Port = 4444,
    [int]$RetrySec = 20,
    [int]$ConnectTimeoutMs = 5000
)

$ErrorActionPreference = 'Continue'
$agentId = "$env:COMPUTERNAME/$env:USERNAME"

function Get-Info {
    $os = try { (Get-CimInstance Win32_OperatingSystem).Caption } catch { '' }
    $ips = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
        ForEach-Object { "$($_.IPAddress)/$($_.PrefixLength) ($($_.InterfaceAlias))" })
    return @(
        "computer : $env:COMPUTERNAME",
        "user     : $env:USERNAME",
        "os       : $os",
        "time     : $(Get-Date -Format o)",
        "ips      : $($ips -join '; ')"
    ) -join "`r`n"
}

function Get-Subnets {
    $out = @()
    $def = try { (Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1).InterfaceIndex } catch { -1 }
    foreach ($a in (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' })) {
        $iface = Get-NetAdapter -InterfaceIndex $a.InterfaceIndex -ErrorAction SilentlyContinue
        $name = if ($iface) { $iface.Name } else { '' }
        $flag = if ($a.InterfaceIndex -eq $def) { ' [default-route]' } else { '' }
        $out += "$($a.IPAddress)/$($a.PrefixLength)  if=$name$flag"
    }
    return ($out -join "`r`n")
}

function Get-Firewall {
    $sb = New-Object System.Text.StringBuilder
    try {
        foreach ($p in (Get-NetFirewallProfile -ErrorAction SilentlyContinue)) {
            [void]$sb.AppendLine("profile $($p.Name): enabled=$($p.Enabled) inbound=$($p.DefaultInboundAction) outbound=$($p.DefaultOutboundAction)")
        }
    } catch { [void]$sb.AppendLine("Get-NetFirewallProfile failed: $($_.Exception.Message)") }
    try {
        [void]$sb.AppendLine('')
        [void]$sb.AppendLine('inbound rules (enabled, TCP, port-related):')
        Get-NetFirewallRule -Enabled True -Direction Inbound -ErrorAction SilentlyContinue |
            Where-Object { $_.Action -eq 'Allow' } |
            ForEach-Object {
                $pf = $_ | Get-NetFirewallPortFilter -ErrorAction SilentlyContinue
                if ($pf -and $pf.Protocol -in @('TCP', 'Any') -and $pf.LocalPort -and $pf.LocalPort -ne 'Any') {
                    [void]$sb.AppendLine("  $($_.DisplayName) | port=$($pf.LocalPort) | profile=$($_.Profile)")
                }
            }
    } catch { [void]$sb.AppendLine("port rules failed: $($_.Exception.Message)") }
    return $sb.ToString()
}

function Get-Listening {
    try {
        $rows = Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Sort-Object LocalPort |
            ForEach-Object {
                $pn = ''
                try { $pn = (Get-Process -Id $_.OwningProcess -ErrorAction SilentlyContinue).ProcessName } catch { }
                "{0,-16}:{1,-6} pid={2,-6} {3}" -f $_.LocalAddress, $_.LocalPort, $_.OwningProcess, $pn
            }
        return ($rows -join "`r`n")
    } catch { return "Get-NetTCPConnection failed: $($_.Exception.Message)" }
}

function Get-Scan {
    $script = Join-Path $PSScriptRoot 'scan-machines.ps1'
    if (-not (Test-Path $script)) { return "scan-machines.ps1 not found next to agent: $script" }
    try {
        $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $script -TimeoutMs 450 2>&1 | Out-String
        return $out
    } catch { return "scan failed: $($_.Exception.Message)" }
}

$handlers = @{
    'info' = { Get-Info }
    'subnets' = { Get-Subnets }
    'ipconfig' = { (ipconfig /all | Out-String) }
    'route' = { (route print | Out-String) }
    'arp' = { (arp -a | Out-String) }
    'neighbor' = { (Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue | Format-Table -AutoSize | Out-String) }
    'listening' = { Get-Listening }
    'netstat' = { (netstat -ano | Out-String) }
    'firewall' = { Get-Firewall }
    'services' = { (Get-Service | Where-Object { $_.Status -eq 'Running' } | Sort-Object Name | Format-Table -AutoSize Name, DisplayName | Out-String) }
    'disk' = { (Get-Volume | Format-Table -AutoSize | Out-String) }
    'hosts' = { (Get-Content "$env:WINDIR\System32\drivers\etc\hosts" -ErrorAction SilentlyContinue | Out-String) }
    'time' = { (Get-Date -Format o) }
    'scan' = { Get-Scan }
}

function Send-Line {
    param($Writer, [string]$Text)
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$Text))
    $Writer.WriteLine($b64)
    $Writer.Flush()
}

Write-Host "[agent] id=$agentId  server=$Server`:$Port  retry=${RetrySec}s" -ForegroundColor Cyan
while ($true) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $client.ConnectAsync($Server, $Port)
        if (-not $t.Wait($ConnectTimeoutMs) -or -not $client.Connected) { throw 'connect timeout' }
        $stream = $client.GetStream()
        $stream.ReadTimeout = 600000
        $stream.WriteTimeout = 60000
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
        $writer = New-Object IO.StreamWriter($stream, [Text.Encoding]::UTF8)
        $writer.AutoFlush = $true
        $writer.WriteLine("IOTT-AGENT $agentId")
        Write-Host "[agent] connected to $Server`:$Port" -ForegroundColor Green
        while ($true) {
            $line = $reader.ReadLine()
            if ($null -eq $line) { break }
            $cmd = $line.Trim().ToLowerInvariant()
            if (-not $cmd) { continue }
            if ($cmd -eq 'quit') { Send-Line $writer 'BYE'; break }
            if ($handlers.ContainsKey($cmd)) {
                Write-Host "[agent] run $cmd" -ForegroundColor DarkGray
                try { $out = & $handlers[$cmd] } catch { $out = "ERR: $($_.Exception.Message)" }
                if ($null -eq $out) { $out = '(no output)' }
                $s = [string]$out
                if ($s.Length -gt 200000) { $s = $s.Substring(0, 200000) + "`r`n[truncated at 200000 chars]" }
                Send-Line $writer $s
            } else {
                Send-Line $writer "DENIED: unknown command '$cmd'. allowed: $($handlers.Keys -join ', ') quit"
            }
        }
    } catch {
        Write-Host "[agent] not connected: $($_.Exception.Message); retry in ${RetrySec}s" -ForegroundColor Yellow
    } finally {
        try { $client.Close() } catch { }
    }
    Start-Sleep -Seconds $RetrySec
}
