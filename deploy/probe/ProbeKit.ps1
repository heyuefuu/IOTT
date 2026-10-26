#Requires -Version 5.1
<#
  ProbeKit.ps1 - shared helpers for on-site probing (dot-source: . "$PSScriptRoot\ProbeKit.ps1")
  ASCII only. Read-only probes: no writes to devices.
  Provides:
    Expand-Cidr, Get-LocalCidrs, Get-DefaultRouteIface
    IottProbe.Scan.Tcp / .TcpMany / .Ping / .PingOne   (C#, parallel, fast)
    Invoke-TcpRaw, Invoke-HttpRaw
    Probe-Any (protocol fingerprint for one host:port)
    New-ProbeResult, Get-SuggestedConfig, Get-ArpTable
#>

$ErrorActionPreference = 'Continue'
if (-not $script:ProbeKitLoaded) { $script:ProbeKitLoaded = $true }

# ---------------------------------------------------------------- C# helpers
if (-not ('IottProbe.Scan' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace IottProbe
{
    public static class Scan
    {
        // Trigger OS ARP resolution for every host with one UDP datagram each.
        // SendARP is unreliable on bridge/NIC-team adapters, so we let the IP stack do it:
        // after this, read Get-NetNeighbor and keep entries that have a real MAC.
        public static int UdpPoke(string[] hosts, int port)
        {
            int n = 0;
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    var payload = new byte[] { 0 };
                    foreach (var h in hosts)
                    {
                        try
                        {
                            IPAddress ip;
                            if (!IPAddress.TryParse(h, out ip)) continue;
                            s.SendTo(payload, new IPEndPoint(ip, port));
                            n++;
                        }
                        catch { }
                    }
                }
            }
            catch { }
            return n;
        }

        public static string[] Tcp(string host, int[] ports, int timeoutMs, int parallel)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                try
                {
                    var addrs = Dns.GetHostAddresses(host);
                    foreach (var a in addrs) { if (a.AddressFamily == AddressFamily.InterNetwork) { ip = a; break; } }
                }
                catch { }
                if (ip == null) return new string[0];
            }
            var results = new ConcurrentBag<int>();
            var sem = new SemaphoreSlim(Math.Max(1, parallel));
            var tasks = new List<Task>();
            foreach (var port in ports)
            {
                sem.Wait();
                int p = port;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                        {
                            var ar = s.BeginConnect(ip, p, null, null);
                            if (ar.AsyncWaitHandle.WaitOne(timeoutMs, false) && s.Connected)
                                results.Add(p);
                            try { s.Close(); } catch { }
                        }
                    }
                    catch { }
                    finally { sem.Release(); }
                }));
            }
            try { Task.WaitAll(tasks.ToArray()); } catch { }
            var arr = results.ToArray();
            Array.Sort(arr);
            var outp = new string[arr.Length];
            for (int i = 0; i < arr.Length; i++) outp[i] = arr[i].ToString();
            return outp;
        }

        public static string[] TcpMany(string[] hosts, int[] ports, int timeoutMs, int parallel)
        {
            var results = new ConcurrentBag<string>();
            var sem = new SemaphoreSlim(Math.Max(1, parallel));
            var tasks = new List<Task>();
            foreach (var h in hosts)
            {
                IPAddress ip;
                if (!IPAddress.TryParse(h, out ip)) continue;
                string hs = h;
                foreach (var port in ports)
                {
                    sem.Wait();
                    int p = port;
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                            {
                                var ar = s.BeginConnect(ip, p, null, null);
                                if (ar.AsyncWaitHandle.WaitOne(timeoutMs, false) && s.Connected)
                                    results.Add(hs + "|" + p.ToString());
                                try { s.Close(); } catch { }
                            }
                        }
                        catch { }
                        finally { sem.Release(); }
                    }));
                }
            }
            try { Task.WaitAll(tasks.ToArray()); } catch { }
            var arr = results.ToArray();
            Array.Sort(arr);
            return arr;
        }

        public static string[] Ping(string[] hosts, int timeoutMs, int parallel)
        {
            var results = new ConcurrentBag<string>();
            var sem = new SemaphoreSlim(Math.Max(1, parallel));
            var tasks = new List<Task>();
            foreach (var h in hosts)
            {
                sem.Wait();
                string host = h;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        using (var p = new Ping())
                        {
                            var r = p.Send(host, timeoutMs);
                            if (r != null && r.Status == IPStatus.Success) results.Add(host);
                        }
                    }
                    catch { }
                    finally { sem.Release(); }
                }));
            }
            try { Task.WaitAll(tasks.ToArray()); } catch { }
            var arr = results.ToArray();
            Array.Sort(arr);
            return arr;
        }

        public static bool PingOne(string host, int timeoutMs)
        {
            try { using (var p = new Ping()) { var r = p.Send(host, timeoutMs); return r != null && r.Status == IPStatus.Success; } }
            catch { return false; }
        }
    }
}
'@
}

# ---------------------------------------------------------------- addressing
function Expand-Cidr {
    param([string]$Cidr, [int]$MaxHosts = 512)
    if ($Cidr -match '^(\d+\.\d+\.\d+\.\d+)/(\d+)$') {
        $ip = [Net.IPAddress]::Parse($Matches[1]); $bits = [int]$Matches[2]
        if ($bits -lt 0 -or $bits -gt 32) { return @() }
        $b = $ip.GetAddressBytes()
        $val = ([uint64]$b[0] -shl 24) -bor ([uint64]$b[1] -shl 16) -bor ([uint64]$b[2] -shl 8) -bor [uint64]$b[3]
        $all = [uint64]4294967295
        $mask = if ($bits -eq 0) { [uint64]0 } else { (($all -shl (32 - $bits)) -band $all) }
        $net = $val -band $mask
        $bcast = $net -bor (($all - $mask) -band $all)
        $start = $net + 1; $end = $bcast - 1
        if ($end -lt $start) { return @() }
        if (($end - $start + 1) -gt $MaxHosts) { $end = $start + $MaxHosts - 1 }
        $out = New-Object System.Collections.Generic.List[string]
        for ($i = $start; $i -le $end; $i++) { $out.Add(([Net.IPAddress]::Parse([string]$i)).ToString()) }
        return $out
    }
    if ($Cidr -match '^(\d+\.\d+\.\d+\.)(\d+)-(\d+)$') {
        $out = @()
        for ($i = [int]$Matches[2]; $i -le [int]$Matches[3]; $i++) { $out += "$($Matches[1])$i" }
        return $out
    }
    return @($Cidr)
}

function Get-DefaultRouteIface {
    try {
        $r = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Sort-Object { $_.RouteMetric + $_.InterfaceMetric } | Select-Object -First 1
        if ($r) { return $r.InterfaceIndex }
    } catch { }
    return -1
}

function Get-LocalCidrs {
    param(
        [string[]]$ExcludePatterns = @('vEthernet', 'Meta', 'Radmin', 'Loopback', 'Hyper-V', 'WSL', 'VirtualBox', 'VMware', 'Bluetooth', 'Tailscale', 'ZeroTier'),
        [switch]$IncludeDefaultRouteIface,
        [switch]$IncludeWlan
    )
    $out = New-Object System.Collections.Generic.List[string]
    $defIface = if ($IncludeDefaultRouteIface) { -1 } else { Get-DefaultRouteIface }
    $ips = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
    foreach ($a in $ips) {
        if ($a.InterfaceIndex -eq $defIface) { continue }
        $iface = Get-NetAdapter -InterfaceIndex $a.InterfaceIndex -ErrorAction SilentlyContinue
        $name = if ($iface) { [string]$iface.Name } else { '' }
        $desc = if ($iface) { [string]$iface.InterfaceDescription } else { '' }
        $skip = $false
        foreach ($p in $ExcludePatterns) { if ($name -match $p -or $desc -match $p) { $skip = $true; break } }
        if (-not $IncludeWlan -and ($name -match 'WLAN|Wi-Fi|Wireless' -or $desc -match 'Wireless|Wi-Fi')) { $skip = $true }
        if ($skip) { continue }
        $prefix = [int]$a.PrefixLength
        $ipStr = [string]$a.IPAddress
        if ($prefix -lt 24) {
            $parts = $ipStr.Split('.')
            $cidr = "$($parts[0]).$($parts[1]).$($parts[2]).0/24"
        } else {
            $cidr = "$ipStr/$prefix"
        }
        if (-not $out.Contains($cidr)) { $out.Add($cidr) | Out-Null }
    }
    return $out
}

# ---------------------------------------------------------------- raw sockets
function Convert-BytesToAscii {
    param([byte[]]$Bytes)
    if (-not $Bytes) { return '' }
    return (-join ($Bytes | ForEach-Object { if ($_ -ge 32 -and $_ -lt 127) { [char]$_ } else { '.' } }))
}

function Convert-BytesToHex {
    param([byte[]]$Bytes, [int]$Max = 64)
    if (-not $Bytes) { return '' }
    $n = [Math]::Min($Bytes.Length, $Max)
    return (($Bytes[0..($n - 1)] | ForEach-Object { $_.ToString('X2') }) -join ' ')
}

function Invoke-TcpRaw {
    param(
        [string]$TargetHost, [int]$TargetPort,
        [byte[]]$Send = $null,
        [int]$TimeoutMs = 1200,
        [int]$MaxRead = 4096
    )
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $c.ConnectAsync($TargetHost, $TargetPort)
        if (-not $t.Wait($TimeoutMs)) { return $null }
        if (-not $c.Connected) { return $null }
        $s = $c.GetStream(); $s.ReadTimeout = $TimeoutMs; $s.WriteTimeout = $TimeoutMs
        if ($Send -and $Send.Length -gt 0) { $s.Write($Send, 0, $Send.Length); $s.Flush() }
        $ms = New-Object System.IO.MemoryStream
        $buf = New-Object byte[] 2048
        try {
            while ($ms.Length -lt $MaxRead) {
                $n = $s.Read($buf, 0, $buf.Length)
                if ($n -le 0) { break }
                $ms.Write($buf, 0, $n)
            }
        } catch { }
        $arr = $ms.ToArray()
        if ($arr.Length -eq 0) { return $null }
        return $arr
    } catch { return $null } finally { try { $c.Close() } catch { } }
}

function Invoke-HttpRaw {
    param([string]$TargetHost, [int]$TargetPort, [string]$Path = '/', [int]$TimeoutMs = 1500, [switch]$Tls)
    $c = New-Object System.Net.Sockets.TcpClient
    try {
        $t = $c.ConnectAsync($TargetHost, $TargetPort)
        if (-not $t.Wait($TimeoutMs)) { return $null }
        if (-not $c.Connected) { return $null }
        $s = $c.GetStream()
        if ($Tls) {
            try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
            $cb = [Net.Security.RemoteCertificateValidationCallback] { param($sd, $cert, $chain, $err) return $true }
            $ssl = New-Object Net.Security.SslStream($s, $false, $cb)
            $ssl.AuthenticateAsClient($TargetHost)
            $s = $ssl
        }
        $s.ReadTimeout = $TimeoutMs; $s.WriteTimeout = $TimeoutMs
        $req = "GET $Path HTTP/1.1`r`nHost: ${TargetHost}:${TargetPort}`r`nUser-Agent: iott-probe`r`nAccept: */*`r`nConnection: close`r`n`r`n"
        $b = [Text.Encoding]::ASCII.GetBytes($req)
        $s.Write($b, 0, $b.Length); $s.Flush()
        $ms = New-Object System.IO.MemoryStream
        $buf = New-Object byte[] 8192
        try {
            while ($ms.Length -lt 131072) {
                $n = $s.Read($buf, 0, $buf.Length)
                if ($n -le 0) { break }
                $ms.Write($buf, 0, $n)
            }
        } catch { }
        $raw = $ms.ToArray()
        if ($raw.Length -eq 0) { return $null }
        $text = [Text.Encoding]::UTF8.GetString($raw)
        $status = 0
        if ($text -match '^HTTP/\d\.\d\s+(\d{3})') { $status = [int]$Matches[1] }
        $server = ''
        if ($text -match '(?im)^Server:\s*(.+)$') { $server = $Matches[1].Trim() }
        $title = ''
        if ($text -match '(?is)<title[^>]*>(.*?)</title>') { $title = ($Matches[1] -replace '\s+', ' ').Trim() }
        $headEnd = $text.IndexOf("`r`n`r`n")
        $body = if ($headEnd -gt 0) { $text.Substring($headEnd + 4) } else { $text }
        if ($body.Length -gt 4096) { $body = $body.Substring(0, 4096) }
        return [pscustomobject]@{ Status = $status; Server = $server; Title = $title; Body = $body; Head = $text }
    } catch { return $null } finally { try { $c.Close() } catch { } }
}

# ---------------------------------------------------------------- probe results
function New-ProbeResult {
    param([string]$HostName, [int]$Port, [string]$Protocol, [string]$Evidence, [string]$Hint, [string]$Kind = 'Unknown')
    return [pscustomobject]@{ Host = $HostName; Port = $Port; Protocol = $Protocol; Evidence = $Evidence; Hint = $Hint; Kind = $Kind }
}

function Get-SuggestedConfig {
    param($Result)
    $h = $Result.Host; $p = $Result.Port
    switch -Wildcard ($Result.Protocol) {
        'FOCAS*' { return "protocol=FOCAS host=$h port=$p  (collection points + program transfer; needs Fwlib64/fwlibe64 next to Host)" }
        'GSK*' { return "protocol=GskWebServer host=$h port=$p extendedProperties{RootUrl=http://${h}:${p}/}" }
        'MTConnect*' { return "protocol=MTConnect host=$h port=$p  (address = dataItemId)" }
        'NCLinkApi*' { return "protocol=NCLinkApi host=$h port=$p extendedProperties{ApiBaseUrl=http://${h}:${p}}" }
        'NC-Link*' { return "protocol=NCLink host=$h port=$p extendedProperties{DeviceGuid=<device guid>}" }
        'ModbusTCP*' { return "protocol=ModbusTCP host=$h port=$p  (address e.g. HR0; robots often use 502 too)" }
        'SiemensS7*' { return "protocol=SiemensS7 host=$h port=$p extendedProperties{Rack=0,Slot=2}" }
        'OpcUa*' { return "protocol=OpcUa host=$h port=$p extendedProperties{UseSecurity=false,EndpointUrl=opc.tcp://${h}:${p}}" }
        'FTP*' { return "transfer={protocol=FTP,host=$h,port=$p}  (attach to the main device for file checks/transfer)" }
        'SMB*' { return "transfer={protocol=SMB,host=$h,port=$p}  (needs share/account)" }
        'NFS*' { return "transfer={protocol=NFS,host=$h,port=$p}" }
        'FANUC Robot*' { return "protocol=FanucRobot host=$h port=$p  (FANUC robot interface, not CNC)" }
        'IOTT Host*' { return "infrastructure: IOTT Host (the target machine itself) http://${h}:${p}" }
        'IOTT*' { return "infrastructure: MachineConnectionApi gateway http://${h}:${p}" }
        'MySQL*' { return "infrastructure: MySQL ${h}:${p}" }
        'InfluxDB*' { return "infrastructure: InfluxDB ${h}:${p}" }
        'MQTT*' { return "infrastructure: MQTT broker ${h}:${p} (NC-Link devices also use 1883)" }
        'EMQX*' { return "infrastructure: EMQX ${h}:${p}" }
        'HTTP*' { return "vendor web server ($($Result.Hint)): confirm protocol; GSK usually 11520, Huazhong NCLinkApi 19001, MTConnect 5000" }
        default { return "(needs manual confirmation) port=$p" }
    }
}

# ---------------------------------------------------------------- fingerprints
function Probe-Ssh {
    param($h, $p, $ms)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -TimeoutMs $ms -MaxRead 256
    if ($b -and $b.Length -gt 4) {
        $s = Convert-BytesToAscii $b
        if ($s -match '^SSH-') {
            $line = ($s -split "`n")[0].Trim()
            return New-ProbeResult $h $p 'SSH' "banner: $line" 'infrastructure (SSH remote admin)' 'Infrastructure'
        }
    }
    return $null
}

function Probe-Ftp {
    param($h, $p, $ms)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -TimeoutMs $ms -MaxRead 128
    if ($b -and $b.Length -gt 3) {
        $s = (Convert-BytesToAscii $b).Trim()
        if ($s -match '^220') {
            $hint = 'device FTP (transfer=FTP for upload/download and file checks)'
            if ($s -match 'Fanuc|FANUC') { $hint = 'FANUC FTP (transfer=FTP)' }
            return New-ProbeResult $h $p 'FTP' "220 banner: $s" $hint 'Transfer'
        }
    }
    return $null
}

function Probe-Smb {
    param($h, $p, $ms)
    $pkt = [byte[]]@(
        0x00, 0x00, 0x00, 0x6A,
        0xFE, 0x53, 0x4D, 0x42, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00,
        0x24, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00
    )
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -Send $pkt -TimeoutMs $ms -MaxRead 256
    if ($b -and $b.Length -ge 8) {
        if (($b[4] -eq 0xFE -and $b[5] -eq 0x53 -and $b[6] -eq 0x4D -and $b[7] -eq 0x42) -or
            ($b[4] -eq 0xFF -and $b[5] -eq 0x53)) {
            return New-ProbeResult $h $p 'SMB' 'SMB NEGOTIATE response (SMB2/SMB1)' 'transfer=SMB (needs share/account)' 'Transfer'
        }
    }
    if ($b) { return New-ProbeResult $h $p 'SMB(unconfirmed)' 'TCP open (no SMB response)' 'port open but protocol/auth restricted' 'Transfer' }
    return $null
}

function Probe-Rdp {
    param($h, $p, $ms)
    $pkt = [byte[]]@(0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -Send $pkt -TimeoutMs $ms -MaxRead 128
    if ($b -and $b.Length -ge 6 -and $b[4] -eq 0x0E -and ($b[5] -eq 0xD0 -or $b[5] -eq 0x80)) {
        return New-ProbeResult $h $p 'RDP' 'TPKT/X.224 response' 'Remote Desktop (infrastructure)' 'Infrastructure'
    }
    return $null
}

function Probe-MySql {
    param($h, $p, $ms)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -TimeoutMs ([Math]::Max($ms, 3000)) -MaxRead 512
    if ($b -and $b.Length -gt 6 -and $b[4] -eq 0x0A) {
        $end = 5
        while ($end -lt $b.Length -and $b[$end] -ne 0) { $end++ }
        $ver = [Text.Encoding]::ASCII.GetString($b, 5, [Math]::Max(0, $end - 5))
        return New-ProbeResult $h $p 'MySQL' "handshake server_version=$ver" 'infrastructure: MySQL' 'Infrastructure'
    }
    return $null
}

function Probe-Mqtt {
    param($h, $p, $ms)
    $cid = 'iott-probe-01'
    $pkt = [byte[]]@(0x10, [byte](12 + $cid.Length), 0x00, 0x04, 0x4D, 0x51, 0x54, 0x54, 0x04, 0x02, 0x00, 0x3C, 0x00, [byte]$cid.Length) + [Text.Encoding]::ASCII.GetBytes($cid)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -Send $pkt -TimeoutMs $ms -MaxRead 16
    if ($b -and $b.Length -ge 4 -and $b[0] -eq 0x20) {
        $rc = $b[3]
        return New-ProbeResult $h $p 'MQTT-Broker' "CONNACK rc=$rc" 'if this is an NC-Link device broker: protocol=NCLink + DeviceGuid' 'Infrastructure'
    }
    return $null
}

function Probe-Modbus {
    param($h, $p, $ms)
    foreach ($unit in 1, 255, 0) {
        $req = [byte[]]@(0x00, 0x01, 0x00, 0x00, 0x00, 0x06, [byte]$unit, 0x03, 0x00, 0x00, 0x00, 0x01)
        $r = Invoke-TcpRaw -TargetHost $h -TargetPort $p -Send $req -TimeoutMs $ms -MaxRead 64
        if ($r -and $r.Length -ge 9 -and $r[7] -eq 0x03) {
            $v = ($r[9] -shl 8) + $r[10]
            return New-ProbeResult $h $p 'ModbusTCP' "FC03 unit=$unit HR0=$v" 'address e.g. HR0 / 40001; unit default 1' 'Device'
        }
        if ($r -and $r.Length -ge 9 -and $r[7] -eq 0x83) {
            return New-ProbeResult $h $p 'ModbusTCP' "FC03 exception 0x$($r[8].ToString('X2')) (unit=$unit)" 'protocol present; adjust unit id' 'Device'
        }
    }
    return $null
}

function Probe-S7 {
    param($h, $p, $ms)
    $cr = [byte[]]@(0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x02, 0xC0, 0x01, 0x0A)
    $r = Invoke-TcpRaw -TargetHost $h -TargetPort $p -Send $cr -TimeoutMs $ms -MaxRead 64
    if ($r -and $r.Length -ge 6 -and $r[5] -eq 0xD0) {
        return New-ProbeResult $h $p 'SiemensS7' 'COTP Connection Confirm' 'extendedProperties Rack/Slot (e.g. Rack=0,Slot=2)' 'Device'
    }
    if ($r -and $r.Length -ge 6 -and $r[5] -eq 0x80) {
        return New-ProbeResult $h $p 'SiemensS7' 'COTP refused (rack/slot mismatch?)' 'try other Rack/Slot' 'Device'
    }
    return $null
}

function Probe-OpcUa {
    param($h, $p, $ms)
    $hel = [byte[]]@(0x48, 0x45, 0x4C, 0x46, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00)
    $r = Invoke-TcpRaw -TargetHost $h -TargetPort $p -Send $hel -TimeoutMs $ms -MaxRead 64
    if ($r -and $r.Length -ge 4 -and $r[0] -eq 0x41 -and $r[1] -eq 0x43 -and $r[2] -eq 0x4B) {
        return New-ProbeResult $h $p 'OpcUa' 'OPC UA HEL/ACK' 'extendedProperties UseSecurity=false; address e.g. ns=2;s=...' 'Device'
    }
    return $null
}

function Probe-Vnc {
    param($h, $p, $ms)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -TimeoutMs $ms -MaxRead 32
    if ($b -and $b.Length -ge 4 -and $b[0] -eq 0x52 -and $b[1] -eq 0x46 -and $b[2] -eq 0x42) {
        return New-ProbeResult $h $p 'VNC' 'RFB banner' 'remote desktop (infrastructure)' 'Infrastructure'
    }
    return $null
}

function Probe-Mtconnect {
    param($h, $p, $ms)
    $r = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/probe' -TimeoutMs $ms
    if ($r -and $r.Status -eq 200 -and $r.Body -match 'MTConnectDevices') {
        $names = [regex]::Matches($r.Body, 'name="([^"]+)"') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique -First 5
        return New-ProbeResult $h $p 'MTConnect' "GET /probe 200; items: $($names -join ', ')" 'address = dataItemId' 'Device'
    }
    return $null
}

function Probe-Focas {
    param($h, $p, $ms, [string]$FocasProbePath)
    $b = Invoke-TcpRaw -TargetHost $h -TargetPort $p -TimeoutMs $ms -MaxRead 1
    if ($null -eq $b) { return $null }
    $evidence = 'TCP open (FOCAS has no text banner)'
    $hint = 'suspected FOCAS2; confirm with FocasProbe.exe or a driver connection'
    if ($FocasProbePath -and (Test-Path $FocasProbePath)) {
        try {
            $exe = $FocasProbePath
            if ($exe -match '\.dll$') {
                $out = & dotnet $exe $h $p 8 2>&1 | Out-String
            } else {
                $out = & $exe $h $p 8 2>&1 | Out-String
            }
            if ($out -match 'CONNECT OK') {
                $type = ''
                if ($out -match "cnc_type='([^']*)'") { $type = $Matches[1] }
                $series = ''
                if ($out -match "series='([^']*)'") { $series = $Matches[1] }
                $evidence = "FOCAS connect OK cnc_type='$type' series='$series'"
                $hint = 'protocol=FOCAS (collection and program transfer both work)'
                return New-ProbeResult $h $p 'FOCAS' $evidence $hint 'Device'
            } elseif ($out -match 'cnc_allclibhndl3 -> rc=(-?\d+)') {
                $rc = $Matches[1]
                $extra = if ($rc -eq '-15') { 'EW_HSSB: FWLIB ethernet driver / search path' } else { 'see FocasError code table' }
                $evidence = "FOCAS connect failed rc=$rc ($extra)"
                return New-ProbeResult $h $p 'FOCAS(port open, connect failed)' $evidence 'check FOCAS2 enabled, IP/port, FWLIB bitness and DLL search path' 'Device'
            }
        } catch { }
    }
    return New-ProbeResult $h $p 'FOCAS(unconfirmed)' $evidence $hint 'Device'
}

function Probe-HttpService {
    param($h, $p, $ms)
    $tls = $p -in @(443, 8443)
    $r = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/' -TimeoutMs $ms -Tls:$tls
    if (-not $r) { return $null }

    $ev = "HTTP $($r.Status) Server='$($r.Server)' Title='$($r.Title)'"
    $body = $r.Body

    if ($r.Status -eq 401 -and ($r.Head -match 'cluster-uuid' -or $body -match 'not authenticated')) {
        return New-ProbeResult $h $p 'InfluxDB(auth required)' "$ev; 401 not authenticated" 'infrastructure: InfluxDB 3 (needs token)' 'Infrastructure'
    }

    if ($body -match 'MTConnect|MTConnectDevices') { return New-ProbeResult $h $p 'MTConnect' "$ev; body contains MTConnect" 'address = dataItemId' 'Device' }
    if ($body -match 'GSK|GSK988|GSK980') { return New-ProbeResult $h $p 'GSK WebServer(suspected)' "$ev; body contains GSK" 'protocol=GskWebServer host/port' 'Device' }

    if ($p -eq 5000) {
        $mt = Probe-Mtconnect $h $p $ms
        if ($mt) { return $mt }
    }
    if ($p -eq 11520 -or ($r.Status -ge 200 -and $r.Status -lt 400)) {
        $mc = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/mc' -TimeoutMs $ms
        $pr = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/params' -TimeoutMs $ms
        if ($mc -and $mc.Status -eq 200 -and $pr -and $pr.Status -eq 200) {
            return New-ProbeResult $h $p 'GSK WebServer' "$ev; /mc 200, /params 200" 'protocol=GskWebServer RootUrl=http://host:11520/' 'Device'
        }
    }
    if ($p -eq 19001) {
        $nc = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/api/tools/check-device' -TimeoutMs $ms
        if ($nc -and $nc.Status -eq 200) { return New-ProbeResult $h $p 'NCLinkApi' "$ev; /api/tools/check-device 200" 'protocol=NCLinkApi' 'Device' }
    }
    if ($p -in @(5173, 8082)) {
        $dev = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/api/Devices' -TimeoutMs $ms
        if ($dev -and $dev.Status -eq 200 -and $dev.Body -match '^\s*\[') {
            $n = ([regex]::Matches($dev.Body, '"id"')).Count
            return New-ProbeResult $h $p 'IOTT Host' "$ev; /api/Devices 200 (id count ~ $n)" 'infrastructure: IOTT Host' 'Infrastructure'
        }
    }
    if ($p -in @(5087, 8090)) {
        $hc = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/api/health' -TimeoutMs $ms
        if (-not $hc) { $hc = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/health' -TimeoutMs $ms }
        if ($hc -and $hc.Status -eq 200) { return New-ProbeResult $h $p 'IOTT gateway' "$ev; /api/health 200" 'infrastructure: MachineConnectionApi gateway' 'Infrastructure' }
    }
    if ($p -eq 8181 -or $body -match 'influxdb') {
        $hl = Invoke-HttpRaw -TargetHost $h -TargetPort $p -Path '/health' -TimeoutMs $ms
        if ($hl -and $hl.Status -eq 200 -and $hl.Body -match 'influxdb') { return New-ProbeResult $h $p 'InfluxDB' "$ev; /health=$($hl.Status)" 'infrastructure: InfluxDB' 'Infrastructure' }
    }
    if ($p -eq 18083 -or $body -match 'emqx|EMQX') {
        if ($body -match 'emqx|EMQX' -or $p -eq 18083) { return New-ProbeResult $h $p 'EMQX' "$ev" 'infrastructure: EMQX dashboard' 'Infrastructure' }
    }

    $hint = switch ($p) {
        11520 { 'suspected GSK WebServer (default port)' }
        19001 { 'suspected Huazhong NC-Link API (default port)' }
        7080 { 'suspected Jingdiao shim' }
        5173 { 'suspected IOTT Host' }
        8082 { 'suspected IOTT web site' }
        8090 { 'suspected device simulator console' }
        8181 { 'suspected InfluxDB' }
        18083 { 'suspected EMQX dashboard' }
        5000 { 'suspected MTConnect agent (default port)' }
        default { 'HTTP service' }
    }
    return New-ProbeResult $h $p 'HTTP' $ev $hint 'Unknown'
}

function Probe-Any {
    param([string]$TargetHost, [int]$TargetPort, [int]$TimeoutMs = 1200, [string]$FocasProbePath)
    try {
        switch ($TargetPort) {
            21 { return Probe-Ftp $TargetHost $TargetPort $TimeoutMs }
            22 { return Probe-Ssh $TargetHost $TargetPort $TimeoutMs }
            23 {
                $b = Invoke-TcpRaw -TargetHost $TargetHost -TargetPort $TargetPort -TimeoutMs $TimeoutMs -MaxRead 128
                if ($b -and (Convert-BytesToAscii $b).Trim().Length -gt 0) {
                    return New-ProbeResult $TargetHost $TargetPort 'Telnet(suspected)' "banner: $((Convert-BytesToAscii $b).Trim())" 'Telnet device/switch; confirm before configuring' 'Infrastructure'
                }
                return $null
            }
            80 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            102 { Probe-S7 $TargetHost $TargetPort $TimeoutMs }
            443 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            445 { Probe-Smb $TargetHost $TargetPort $TimeoutMs }
            139 { Probe-Smb $TargetHost $TargetPort $TimeoutMs }
            502 { Probe-Modbus $TargetHost $TargetPort $TimeoutMs }
            1883 { Probe-Mqtt $TargetHost $TargetPort $TimeoutMs }
            2049 { New-ProbeResult $TargetHost $TargetPort 'NFS' 'TCP open' 'transfer=NFS (needs exported share)' 'Transfer' }
            3306 { Probe-MySql $TargetHost $TargetPort $TimeoutMs }
            3307 { Probe-MySql $TargetHost $TargetPort $TimeoutMs }
            3389 { Probe-Rdp $TargetHost $TargetPort $TimeoutMs }
            4840 { Probe-OpcUa $TargetHost $TargetPort $TimeoutMs }
            5000 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            5006 { New-ProbeResult $TargetHost $TargetPort 'MC(suspected)' 'TCP open' 'Mitsubishi MC port; confirm' 'Unknown' }
            5007 { New-ProbeResult $TargetHost $TargetPort 'MC/Mewtocol(suspected)' 'TCP open' 'Mitsubishi MC / Panasonic Mewtocol port' 'Unknown' }
            5173 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            5087 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            5900 { Probe-Vnc $TargetHost $TargetPort $TimeoutMs }
            5985 { New-ProbeResult $TargetHost $TargetPort 'WinRM(HTTP)' 'TCP open' 'PowerShell remoting available (needs credentials)' 'Infrastructure' }
            5986 { New-ProbeResult $TargetHost $TargetPort 'WinRM(HTTPS)' 'TCP open' 'PowerShell remoting available (needs credentials)' 'Infrastructure' }
            7080 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            8000 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            8080 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            8082 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            8090 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            8193 { Probe-Focas $TargetHost $TargetPort $TimeoutMs $FocasProbePath }
            8181 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            9600 { New-ProbeResult $TargetHost $TargetPort 'FINS(suspected)' 'TCP open' 'Omron FINS default port (scout-devices.ps1 can deep-probe)' 'Device' }
            9999 { New-ProbeResult $TargetHost $TargetPort 'HaasMdc(suspected)' 'TCP open' 'Haas MDC default port (scout-devices.ps1 can deep-probe)' 'Device' }
            11520 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            18083 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            19001 { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
            60008 { New-ProbeResult $TargetHost $TargetPort 'FANUC Robot(suspected)' 'TCP open' 'FANUC robot interface default port (not CNC)' 'Device' }
            default { Probe-HttpService $TargetHost $TargetPort $TimeoutMs }
        }
    } catch { return $null }
}

# ---------------------------------------------------------------- ARP helpers
function Get-ArpTable {
    $rows = @()
    try {
        foreach ($n in (Get-NetNeighbor -AddressFamily IPv4 -ErrorAction SilentlyContinue)) {
            if ($n.State -in @('Reachable', 'Stale', 'Delay', 'Probe', 'Permanent') -and $n.LinkLayerAddress -and $n.LinkLayerAddress -ne '00-00-00-00-00-00') {
                $rows += [pscustomobject]@{ IP = $n.IPAddress; MAC = $n.LinkLayerAddress; State = $n.State; Iface = $n.InterfaceAlias }
            }
        }
    } catch { }
    return $rows
}
