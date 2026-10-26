$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'gskdiag'
New-Item -ItemType Directory -Force -Path $out | Out-Null
"output dir = $out"

# 1) 设备清单（含 GskWebServer 设备配置）
$h = (Invoke-WebRequest 'http://localhost:5173/api/Devices' -UseBasicParsing -TimeoutSec 30).Content
[System.IO.File]::WriteAllText("$out\devices.json", $h, (New-Object System.Text.UTF8Encoding($false)))
$hj = $h | ConvertFrom-Json
$gsk = $hj | Where-Object { $_.protocol -eq 'GskWebServer' }
"GskWebServer devices = $($gsk.Count)"
$lines = @()
foreach ($d in $gsk) {
    $lines += "id=$($d.id)  name=$($d.name)  host=$($d.host):$($d.port)  status=$($d.status)"
    $props = $d.extendedProperties
    if ($props) { foreach ($pn in $props.PSObject.Properties.Name) { $lines += "    ext[$pn]=$($props.$pn)" } }
}
$dev = $gsk | Select-Object -First 1
$lines += ""
$lines += "选用设备: id=$($dev.id) host=$($dev.host):$($dev.port)"

# 2) 采集配置（点位）
$cfg = $null
try { $cfg = (Invoke-WebRequest "http://localhost:5173/api/collection-config/$($dev.id)" -UseBasicParsing -TimeoutSec 30).Content } catch { $lines += "collection-config 读取失败: $($_.Exception.Message)" }
if ($cfg) {
    [System.IO.File]::WriteAllText("$out\collection-config.json", $cfg, (New-Object System.Text.UTF8Encoding($false)))
    $cj = $cfg | ConvertFrom-Json
    foreach ($p in $cj) { foreach ($g in $p.groups) { foreach ($t in $g.tags) { $lines += "  点位: group=$($g.groupName) addr=$($t.address) type=$($t.dataType) name=$($t.displayName)" } } }
}

# 3) 真机 WebServer 探测：各候选路径的 GET vs POST 允许情况
$lines += ""
$lines += "=== 真机 WebServer 探测 host=$($dev.host):$($dev.port) ==="
$root = if ($dev.extendedProperties -and $dev.extendedProperties.CncRootPath) { $dev.extendedProperties.CncRootPath } else { '/api/v1/cnc' }
$sn = if ($dev.extendedProperties -and $dev.extendedProperties.DeviceSn) { $dev.extendedProperties.DeviceSn } else { 'cnc' }
$paths = @("$root/mc", "$root/macro-values", "$root/params", "$root/plc/regs", "$root/info/diags", "$root/info/alarms", "$root/mc/gcode", "$root/file", '/api/v1/workshop', '/swagger/index.html', '/openapi.json', '/swagger/v1/swagger.json')
$results = @()
foreach ($p in $paths) {
    foreach ($m in 'GET', 'POST') {
        $u = "http://$($dev.host):$($dev.port)$p"
        $st = ''; $allow = ''; $snip = ''
        try {
            if ($m -eq 'GET') { $r = Invoke-WebRequest -Uri $u -Method Get -UseBasicParsing -TimeoutSec 8 }
            else { $r = Invoke-WebRequest -Uri $u -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing -TimeoutSec 8 }
            $st = [int]$r.StatusCode; $allow = $r.Headers['Allow']; $snip = ($r.Content -replace '\s+', ' ')
        } catch {
            $resp = $null; try { $resp = $_.Exception.Response } catch { }
            if ($resp -and $resp.StatusCode) { $st = [int]$resp.StatusCode; try { $allow = $resp.Headers['Allow'] } catch { }; try { $sr = New-Object IO.StreamReader($resp.GetResponseStream()); $snip = ($sr.ReadToEnd() -replace '\s+', ' ') } catch { } }
            else { $st = "ERR:$($_.Exception.Message)" }
        }
        if ($snip -and $snip.Length -gt 160) { $snip = $snip.Substring(0, 160) }
        $results += [pscustomobject]@{ Path = $p; Method = $m; Status = $st; Allow = $allow; Body = $snip }
    }
}
foreach ($r in $results) { $lines += ("  [{0,-4}] {1,-40} -> {2}  Allow={3}  {4}" -f $r.Method, $r.Path, $r.Status, $r.Allow, $r.Body) }

# 4) 复现 Host 读点位
if ($cj) {
    $firstTag = $null
    foreach ($p in $cj) { foreach ($g in $p.groups) { foreach ($t in $g.tags) { if (-not $firstTag) { $firstTag = $t } } } }
    if ($firstTag) {
        $lines += ""
        $lines += "=== 复现 Host 读点位 addr=$($firstTag.address) type=$($firstTag.dataType) ==="
        $body = @{ tags = @(@{ address = $firstTag.address; dataType = $firstTag.dataType }) } | ConvertTo-Json -Depth 5 -Compress
        try {
            $r = Invoke-WebRequest "http://localhost:5173/api/data/$($dev.id)/read" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -UseBasicParsing -TimeoutSec 20
            $lines += "  Host read -> HTTP $($r.StatusCode): $($r.Content)"
        } catch {
            $code = $_.Exception.Response.StatusCode.value__; $b = ''
            try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $b = $sr.ReadToEnd() } catch { }
            $lines += "  Host read -> HTTP $code  $b"
        }
    }
}

# 5) Host 日志中的 Gsk/405
$lines += ""
$lines += "=== Host 日志 Gsk/405 最近 10 条 ==="
$log = Get-ChildItem 'C:\iott-bundle\host\logs' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) { Select-String -Path $log.FullName -Pattern 'Gsk|GSK|405|Method Not Allowed' -ErrorAction SilentlyContinue | Select-Object -Last 10 | ForEach-Object { $lines += "  " + $_.Line } }

[System.IO.File]::WriteAllLines("$out\report.txt", $lines, (New-Object System.Text.UTF8Encoding($false)))
"report written: $out\report.txt"
