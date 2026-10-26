#Requires -Version 5.1
<#
.SYNOPSIS
  真机接入前的设备探查：扫描工业端口 + 协议指纹识别 + 输出建议配置。
.DESCRIPTION
  用于"拿到真机"时先摸底：对给定 IP（支持列表/区间/CIDR）扫描常见工业端口，
  并对开放端口做协议级握手识别（Modbus/S7/OPC UA/MTConnect/FTP/MQTT/FINS/Haas/Mewtocol/HTTP 等），
  最后打印"建议的设备配置"（protocol/host/port/extendedProperties 提示）并可导出 JSON。

  仅做只读探测：Modbus 只读 FC03、S7 只发 COTP CR、OPC UA 只发 HEL、MTConnect 只 GET /probe、
  FTP 只读 banner/SYST、MQTT 只 CONNECT/CONNACK、FINS 只握手、Haas/Mewtocol 只读命令。不写任何点位。

.EXAMPLE
  .\scout-devices.ps1 -Hosts 192.168.1.50
.EXAMPLE
  .\scout-devices.ps1 -Hosts 192.168.1.10-20,192.168.1.100 -Json .\scout.json
.EXAMPLE
  .\scout-devices.ps1 -Hosts 192.168.1.0/24 -TimeoutMs 600 -ScanOnly
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Hosts,
    [int[]]$Ports = @(502, 102, 4840, 5000, 21, 9600, 1883, 8193, 19001, 11520, 5007, 9999, 7080, 80, 443, 8080, 445, 2049),
    [int]$TimeoutMs = 1200,
    [int]$Parallel = 64,
    [switch]$ScanOnly,
    [string]$Json,
    [string]$Markdown
)

$ErrorActionPreference = 'Continue'

# ---------- 工具 ----------
function Expand-Hosts([string[]]$specs) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($s in $specs) {
        foreach ($item in ($s -split ',')) {
            $t = $item.Trim(); if (-not $t) { continue }
            if ($t -match '^(\d+\.\d+\.\d+\.\d+)/(\d+)$') {
                $ip = [System.Net.IPAddress]::Parse($Matches[1]); $bits = [int]$Matches[2]
                $bytes = $ip.GetAddressBytes()
                $val = ([uint32]$bytes[0] -shl 24) + ([uint32]$bytes[1] -shl 16) + ([uint32]$bytes[2] -shl 8) + [uint32]$bytes[3]
                $mask = if ($bits -eq 0) { [uint32]0 } else { [uint32]0xFFFFFFFF -shl (32 - $bits) }
                $start = $val -band $mask; $end = $start + (([uint32]0xFFFFFFFF -band (-bnot $mask)))
                for ($i = $start + 1; $i -lt $end; $i++) { $out.Add(([System.Net.IPAddress]::Parse([string]$i)).ToString()) | Out-Null }
                continue
            }
            if ($t -match '^(\d+\.\d+\.\d+\.)(\d+)-(\d+)$') {
                for ($i = [int]$Matches[2]; $i -le [int]$Matches[3]; $i++) { $out.Add("$($Matches[1])$i") | Out-Null }
                continue
            }
            $out.Add($t) | Out-Null
        }
    }
    return $out | Select-Object -Unique
}

function Test-Tcp([string]$h, [int]$p, [int]$ms) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $c.ConnectAsync($h, $p)
        if (-not $t.Wait($ms)) { return $false }
        return $c.Connected
    } catch { return $false } finally { try { $c.Close() } catch { } }
}

function Invoke-TcpProbe([string]$h, [int]$p, [byte[]]$send, [int]$ms, [int]$maxRead = 256) {
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $c.ConnectAsync($h, $p); if (-not $t.Wait($ms)) { return $null }
        $s = $c.GetStream(); $s.ReadTimeout = $ms; $s.WriteTimeout = $ms
        if ($send) { $s.Write($send, 0, $send.Length); $s.Flush() }
        $buf = New-Object byte[] $maxRead
        try { $n = $s.Read($buf, 0, $buf.Length) } catch { return $null }
        if ($n -le 0) { return $null }
        return $buf[0..($n - 1)]
    } catch { return $null } finally { try { $c.Close() } catch { } }
}

function Invoke-Http([string]$h, [int]$p, [string]$path, [int]$ms) {
    $u = "http://${h}:${p}${path}"
    try {
        $r = Invoke-WebRequest -Uri $u -UseBasicParsing -TimeoutSec ([Math]::Max(1, [int]($ms / 1000)))
        return @{ Status = [int]$r.StatusCode; Server = $r.Headers['Server']; Body = $r.Content }
    } catch {
        # 非 2xx（PS5.1 会抛异常）也要识别出"这是 HTTP 服务"
        $resp = $null; try { $resp = $_.Exception.Response } catch { }
        if ($resp -and $resp.StatusCode) {
            $body = ''; try { $sr = New-Object IO.StreamReader($resp.GetResponseStream()); $body = $sr.ReadToEnd() } catch { }
            $server = $null; try { $server = $resp.Headers['Server'] } catch { }
            return @{ Status = [int]$resp.StatusCode; Server = $server; Body = $body }
        }
        return $null
    }
}

function Hex([byte[]]$b) { ($b | ForEach-Object { $_.ToString('X2') }) -join '' }
function Ascii([byte[]]$b) { -join ($b | ForEach-Object { if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '.' } }) }

# ---------- 协议探测 ----------
function Probe-Modbus([string]$h, [int]$p, [int]$ms) {
    foreach ($unit in 1, 255, 0) {
        $req = [byte[]]@(0x00, 0x01, 0x00, 0x00, 0x00, 0x06, [byte]$unit, 0x03, 0x00, 0x00, 0x00, 0x01)
        $r = Invoke-TcpProbe $h $p $req $ms 64
        if ($r -and $r.Length -ge 9 -and $r[7] -eq 0x03) {
            $val = ($r[9] -shl 8) + $r[10]
            return [pscustomobject]@{ Protocol = 'ModbusTCP'; Evidence = "FC03 unit=$unit 读HR0 成功，值=$val"; Hint = "address 形如 HR0 / 40001；unit 默认 1" }
        }
        if ($r -and $r.Length -ge 9 -and $r[7] -eq 0x83) {
            return [pscustomobject]@{ Protocol = 'ModbusTCP'; Evidence = "FC03 返回异常码 0x$($r[8].ToString('X2'))（unit=$unit，协议存在）"; Hint = "unit 需调整；address 形如 HR0" }
        }
    }
    return $null
}

function Probe-S7([string]$h, [int]$p, [int]$ms) {
    $cr = [byte[]]@(0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x02, 0xC0, 0x01, 0x0A)
    $r = Invoke-TcpProbe $h $p $cr $ms 64
    if ($r -and $r.Length -ge 6 -and $r[5] -eq 0xD0) { return [pscustomobject]@{ Protocol = 'SiemensS7'; Evidence = 'COTP Connection Confirm（11 D0）'; Hint = 'extendedProperties Rack/Slot（如 Rack=0,Slot=2）；address 形如 DB1.DBW0' } }
    if ($r -and $r.Length -ge 6 -and $r[5] -eq 0x80) { return [pscustomobject]@{ Protocol = 'SiemensS7'; Evidence = 'COTP 拒绝（可能 rack/slot 不符）'; Hint = '尝试其它 Rack/Slot' } }
    return $null
}

function Probe-OpcUa([string]$h, [int]$p, [int]$ms) {
    # HEL: 'H','E','L','F' + len(32) + protoVer/recv/send/maxMsg/maxChunk(=0/65536/65536/0/0) + urlLen(0)
    $hel = [byte[]]@(0x48, 0x45, 0x4C, 0x46, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
    $r = Invoke-TcpProbe $h $p $hel $ms 64
    if ($r -and $r.Length -ge 4 -and $r[0] -eq 0x41 -and $r[1] -eq 0x43 -and $r[2] -eq 0x4B) { return [pscustomobject]@{ Protocol = 'OpcUa'; Evidence = 'OPC UA HEL/ACK 握手成功'; Hint = '若为匿名/None 端点需 extendedProperties UseSecurity=false；address 形如 ns=2;s=...（ns 索引以端点为准）' } }
    return $null
}

function Probe-Mtconnect([string]$h, [int]$p, [int]$ms) {
    $r = Invoke-Http $h $p '/probe' $ms
    if ($r -and $r.Status -eq 200 -and $r.Body -match 'MTConnectDevices') {
        $names = [regex]::Matches($r.Body, 'name="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique -First 5
        return [pscustomobject]@{ Protocol = 'MTConnect'; Evidence = "GET /probe 成功，设备/项: $($names -join ', ')"; Hint = 'address 用 dataItemId（如 xpos）；先看 /probe 的 DataItem id' }
    }
    return $null
}

function Probe-Ftp([string]$h, [int]$p, [int]$ms) {
    $banner = Invoke-TcpProbe $h $p $null $ms 128
    if ($banner -and (Ascii $banner) -match '^220') {
        $syst = Invoke-TcpProbe $h $p ([Text.Encoding]::ASCII.GetBytes("SYST`r`n")) $ms 128
        return [pscustomobject]@{ Protocol = 'FTP'; Evidence = "220 banner: $((Ascii $banner).Trim())"; Hint = '可作为设备 transfer（文件传输）；用户名/密码按现场' }
    }
    return $null
}

function Probe-Mqtt([string]$h, [int]$p, [int]$ms) {
    $cid = 'scout-0001'
    $connect = [byte[]]@(0x10, 0x16, 0x00, 0x04, 0x4D, 0x51, 0x54, 0x54, 0x04, 0x02, 0x00, 0x3C, 0x00, [byte]$cid.Length) + [Text.Encoding]::ASCII.GetBytes($cid)
    $r = Invoke-TcpProbe $h $p $connect $ms 16
    if ($r -and $r.Length -ge 4 -and $r[0] -eq 0x20) {
        $rc = $r[3]
        return [pscustomobject]@{ Protocol = 'MQTT-Broker'; Evidence = "CONNACK rc=$rc"; Hint = '若为 NC-Link 设备 Broker：protocol=NCLink + extendedProperties DeviceGuid=...（需设备 guid）' }
    }
    return $null
}

function Probe-Fins([string]$h, [int]$p, [int]$ms) {
    # FINS/TCP 头部的长度/命令字段大小端在不同实现不一致（Omron 实机多为大端）——两种都试
    foreach ($be in $true, $false) {
        $len = if ($be) { [byte[]]@(0x00, 0x00, 0x00, 0x0C) } else { [byte[]]@(0x0C, 0x00, 0x00, 0x00) }
        $zero = [byte[]]@(0x00, 0x00, 0x00, 0x00)
        $node = if ($be) { [byte[]]@(0x00, 0x00, 0x00, 0x01) } else { [byte[]]@(0x01, 0x00, 0x00, 0x00) }
        $req = [byte[]]@(0x46, 0x49, 0x4E, 0x53) + $len + $zero + $zero + $node
        $r = Invoke-TcpProbe $h $p $req $ms 64
        if ($r -and $r.Length -ge 16 -and $r[0] -eq 0x46 -and $r[1] -eq 0x49 -and $r[2] -eq 0x4E) {
            $endian = if ($be) { '大端' } else { '小端' }
            return [pscustomobject]@{ Protocol = 'FINS'; Evidence = "FINS/TCP 节点地址交换成功（${endian}头部）"; Hint = 'address 形如 DM100 / CIO0；区码按设备' }
        }
    }
    return $null
}

function Probe-Haas([string]$h, [int]$p, [int]$ms) {
    $r = Invoke-TcpProbe $h $p ([Text.Encoding]::ASCII.GetBytes("Q100`r`n")) $ms 256
    if ($r -and (Ascii $r) -match 'Q\d{3}') { return [pscustomobject]@{ Protocol = 'HaasMdc'; Evidence = "响应: $((Ascii $r).Trim().Substring(0,[Math]::Min(60,(Ascii $r).Trim().Length)))"; Hint = 'address 形如 Q100/Q104（只读）' } }
    return $null
}

function Probe-Mewtocol([string]$h, [int]$p, [int]$ms) {
    $r = Invoke-TcpProbe $h $p ([Text.Encoding]::ASCII.GetBytes("%01#RDD001000010055`r")) $ms 256
    if ($r -and (Ascii $r) -match '^%01') { return [pscustomobject]@{ Protocol = 'Mewtocol'; Evidence = "响应: $((Ascii $r).Trim())"; Hint = 'address 形如 R0 / DT0（按型号）' } }
    return $null
}

function Probe-HttpAny([string]$h, [int]$p, [int]$ms) {
    $r = Invoke-Http $h $p '/' $ms
    if ($r) {
        $snip = ($r.Body -replace '\s+', ' ')
        if ($snip.Length -gt 80) { $snip = $snip.Substring(0, 80) }
        $guess = switch ($p) {
            11520 { '疑似 GSK WebServer' }
            7080 { '疑似 精雕 shim' }
            19001 { '疑似 华中 NC-Link API' }
            8080 { '通用/厂商 WebServer' }
            80 { '通用/厂商 WebServer' }
            443 { 'HTTPS 服务' }
            default { 'HTTP 服务' }
        }
        return [pscustomobject]@{ Protocol = 'HTTP'; Evidence = "HTTP $($r.Status) Server=$($r.Server) body='$snip'"; Hint = "$guess（按默认端口推断）；需用其专有路径/驱动确认" }
    }
    return $null
}

function Probe-NcLinkApi([string]$h, [int]$p, [int]$ms) {
    $r = Invoke-Http $h $p '/api/tools/check-device' $ms
    if ($r -and $r.Status -eq 200) { return [pscustomobject]@{ Protocol = 'NCLinkApi'; Evidence = 'GET /api/tools/check-device 200'; Hint = 'address 用 DataItem key；协议 NCLinkApi' } }
    return $null
}

function Probe-One([string]$h, [int]$p, [int]$ms) {
    switch ($p) {
        502 { return Probe-Modbus $h $p $ms }
        102 { return Probe-S7 $h $p $ms }
        4840 { return Probe-OpcUa $h $p $ms }
        5000 { return Probe-Mtconnect $h $p $ms }
        21 { return Probe-Ftp $h $p $ms }
        1883 { return Probe-Mqtt $h $p $ms }
        9600 { return Probe-Fins $h $p $ms }
        9999 { return Probe-Haas $h $p $ms }
        5007 { return Probe-Mewtocol $h $p $ms }
        19001 { return Probe-NcLinkApi $h $p $ms }
        8193 { return [pscustomobject]@{ Protocol = 'FOCAS(待确认)'; Evidence = 'TCP 开放'; Hint = 'FOCAS/FWLIB 需厂家客户端与授权，端口开放仅代表可达' } }
        445 { return [pscustomobject]@{ Protocol = 'SMB(待确认)'; Evidence = 'TCP 开放'; Hint = '可作为 transfer=SMB；需用户名/密码/共享' } }
        2049 { return [pscustomobject]@{ Protocol = 'NFS(待确认)'; Evidence = 'TCP 开放'; Hint = '可作为 transfer=NFS（需已挂载/导出）' } }
        default { return Probe-HttpAny $h $p $ms }
    }
}

# ---------- 主流程 ----------
$ipList = Expand-Hosts $Hosts
Write-Host "[scout] 目标 $($ipList.Count) 个地址，端口 $($Ports.Count) 个，超时 ${TimeoutMs}ms" -ForegroundColor Cyan

# 1) 端口扫描（runspace 池并行）
$open = New-Object System.Collections.Generic.List[object]
$pool = [runspacefactory]::CreateRunspacePool(1, [Math]::Max(1, $Parallel)); $pool.Open()
$jobs = New-Object System.Collections.Generic.List[object]
foreach ($h in $ipList) {
    foreach ($p in $Ports) {
        $ps = [powershell]::Create(); $ps.RunspacePool = $pool
        [void]$ps.AddScript({
                param($h, $p, $ms)
                $c = New-Object System.Net.Sockets.TcpClient
                try { $t = $c.ConnectAsync($h, $p); if ($t.Wait($ms) -and $c.Connected) { "$h|$p" } } catch { } finally { try { $c.Close() } catch { } }
            }).AddArgument($h).AddArgument($p).AddArgument($TimeoutMs)
        $jobs.Add([pscustomobject]@{ PS = $ps; H = $h; P = $p; Handle = $ps.BeginInvoke() })
    }
}
foreach ($j in $jobs) {
    $res = $j.PS.EndInvoke($j.Handle)
    if ($res) { $open.Add([pscustomobject]@{ Host = $j.H; Port = $j.P }) | Out-Null }
    $j.PS.Dispose()
}
$pool.Close(); $pool.Dispose()
Write-Host "[scout] 开放端口 $($open.Count) 个" -ForegroundColor Cyan

# 2) 协议指纹
$results = New-Object System.Collections.Generic.List[object]
foreach ($o in $open) {
    $info = $null
    if (-not $ScanOnly) { $info = Probe-One $o.Host $o.Port $TimeoutMs }
    $results.Add([pscustomobject]@{
            Host = $o.Host; Port = $o.Port
            Identified = if ($info) { $info.Protocol } else { 'TCP(未识别)' }
            Evidence = if ($info) { $info.Evidence } else { '端口开放' }
            Hint = if ($info) { $info.Hint } else { '' }
        }) | Out-Null
    $color = if ($info) { 'Green' } else { 'DarkGray' }
    Write-Host ("  {0,-16} :{1,-6} {2,-16} {3}" -f $o.Host, $o.Port, $results[-1].Identified, $results[-1].Evidence) -ForegroundColor $color
}

# 3) 按主机汇总
Write-Host "`n===== 汇总 / 建议配置 =====" -ForegroundColor Cyan
$byHost = $results | Group-Object Host
foreach ($g in $byHost) {
    Write-Host "`n[$($g.Name)]" -ForegroundColor Yellow
    foreach ($r in ($g.Group | Sort-Object Port)) {
        $suggest = switch -Wildcard ($r.Identified) {
            'Modbus*' { "protocol=ModbusTCP  host=$($r.Host)  port=$($r.Port)" }
            'SiemensS7*' { "protocol=SiemensS7  host=$($r.Host)  port=$($r.Port)  extendedProperties{Rack=0,Slot=?}" }
            'OpcUa*' { "protocol=OpcUa  host=$($r.Host)  port=$($r.Port)  extendedProperties{UseSecurity=false,EndpointUrl=opc.tcp://$($r.Host):$($r.Port)}" }
            'MTConnect*' { "protocol=MTConnect  host=$($r.Host)  port=$($r.Port)" }
            'FINS*' { "protocol=FINS  host=$($r.Host)  port=$($r.Port)" }
            'Haas*' { "protocol=HaasMdc  host=$($r.Host)  port=$($r.Port)" }
            'Mewtocol*' { "protocol=Mewtocol  host=$($r.Host)  port=$($r.Port)" }
            'NCLinkApi*' { "protocol=NCLinkApi  host=$($r.Host)  port=$($r.Port)" }
            'FTP*' { "transfer={protocol=FTP,host=$($r.Host),port=$($r.Port)}（挂到主设备上）" }
            'MQTT-Broker*' { "疑似 NC-Link：protocol=NCLink  port=$($r.Port)  extendedProperties{DeviceGuid=<设备guid>,MqttBrokerPort=$($r.Port)}" }
            'HTTP*' { "HTTP 服务（$($r.Hint)）：确认厂商协议后再定 protocol；GSK 站常用 11520、精雕 7080、华中 NCLinkApi 19001" }
            'FOCAS*' { "疑似 FOCAS：port=$($r.Port)（需 FWLIB 客户端验证）" }
            default { "（需人工确认协议）port=$($r.Port)" }
        }
        Write-Host ("  {0,-6} {1,-16} -> {2}" -f $r.Port, $r.Identified, $suggest)
    }
}

if ($Json) {
    $payload = [pscustomobject]@{ generatedAt = (Get-Date -Format o); hostCount = $ipList.Count; openPortCount = $open.Count; results = $results }
    $payload | ConvertTo-Json -Depth 6 | Set-Content -Path $Json -Encoding UTF8
    Write-Host "`n[scout] JSON 已写入 $Json" -ForegroundColor Cyan
}
if ($Markdown) {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("# 设备探查报告")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("- 生成时间：$(Get-Date -Format o)")
    [void]$sb.AppendLine("- 地址数：$($ipList.Count)；开放端口：$($open.Count)")
    [void]$sb.AppendLine()
    [void]$sb.AppendLine("| 主机 | 端口 | 识别 | 证据 | 建议 |")
    [void]$sb.AppendLine("|---|---|---|---|---|")
    foreach ($r in $results) { [void]$sb.AppendLine("| $($r.Host) | $($r.Port) | $($r.Identified) | $($r.Evidence -replace '\|','/') | $($r.Hint -replace '\|','/') |") }
    Set-Content -Path $Markdown -Value $sb.ToString() -Encoding UTF8
    Write-Host "[scout] Markdown 已写入 $Markdown" -ForegroundColor Cyan
}
