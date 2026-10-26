$ErrorActionPreference = 'Continue'
$out = 'D:\gskdiag3'
try { New-Item -ItemType Directory -Force -Path $out -ErrorAction Stop | Out-Null } catch { $out = Join-Path $env:TEMP 'gskdiag3'; New-Item -ItemType Directory -Force -Path $out | Out-Null }
$lines = New-Object System.Collections.Generic.List[string]
function W($s) { $lines.Add([string]$s) | Out-Null; Write-Host $s }
W "output dir = $out"

# ---------- 1) 405 异常上下文（含堆栈），定位驱动与 URL ----------
W ""
W "########## 1) Host 日志中 405 异常的上下文 ##########"
$log = Get-ChildItem 'C:\iott-bundle\host\logs' -Filter 'industrial-iot-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    W "log = $($log.FullName)"
    $all = Get-Content $log.FullName -Encoding UTF8
    $idx = @()
    for ($i = 0; $i -lt $all.Count; $i++) { if ($all[$i] -match '405 \(Method Not Allowed\)') { $idx += $i } }
    W "405 命中 $($idx.Count) 处；打印最后 5 处上下文（前 10 行 + 后 25 行）"
    foreach ($i in ($idx | Select-Object -Last 5)) {
        W "---- 行 $i ----"
        $s = [Math]::Max(0, $i - 10); $e = [Math]::Min($all.Count - 1, $i + 25)
        for ($k = $s; $k -le $e; $k++) { W ("  " + $all[$k]) }
    }
} else { W "未找到 industrial-iot-*.log" }

# ---------- 2) 当前采集任务 / 点位配置 ----------
W ""
W "########## 2) 采集任务与点位配置 ##########"
try { W ("[/api/collection/status] " + (Invoke-WebRequest 'http://localhost:5173/api/collection/status' -UseBasicParsing -TimeoutSec 15).Content) } catch { W "collection/status 失败" }
$devs = $null
try { $devs = (Invoke-WebRequest 'http://localhost:5173/api/Devices' -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json } catch { W "Devices 读取失败" }
if ($devs) {
    W "设备数=$($devs.Count)"
    foreach ($d in $devs) {
        $cfgTxt = ''
        try { $cfgTxt = (Invoke-WebRequest "http://localhost:5173/api/collection-config/$($d.id)" -UseBasicParsing -TimeoutSec 10).Content } catch { $cfgTxt = '' }
        if ($cfgTxt -and $cfgTxt -ne '[]') {
            W ("  DEV {0}  [{1}]  {2}:{3}  name={4}" -f $d.id, $d.protocol, $d.host, $d.port, $d.name)
            try { $cj = $cfgTxt | ConvertFrom-Json; foreach ($p in @($cj)) { foreach ($g in @($p.groups)) { foreach ($t in @($g.tags)) { W ("      point: group={0} addr={1} type={2}" -f $g.groupName, $t.address, $t.dataType) } } } } catch { W "      (点位解析失败)" }
        }
    }
}

# ---------- 3) 复现：GSK 机床 test-connection + 读点 ----------
W ""
W "########## 3) 复现 GSK 机床 ##########"
$gsk = $null
if ($devs) { $gsk = @($devs | Where-Object { $_.protocol -eq 'GskWebServer' -and $_.host -match '^\d{1,3}(\.\d{1,3}){3}$' }) | Where-Object { $_.host -eq '192.168.1.165' } | Select-Object -First 1 }
if (-not $gsk -and $devs) { $gsk = @($devs | Where-Object { $_.protocol -eq 'GskWebServer' })[0] }
if ($gsk) {
    W "GSK 设备: $($gsk.id)  $($gsk.host):$($gsk.port)  $($gsk.name)"
    try {
        $r = Invoke-WebRequest "http://localhost:5173/api/Devices/$($gsk.id)/test-connection" -Method Post -UseBasicParsing -TimeoutSec 30
        W ("  test-connection -> HTTP {0}  {1}" -f $r.StatusCode, ($r.Content -replace '\s+', ' '))
    } catch { W ("  test-connection -> HTTP {0}" -f $_.Exception.Response.StatusCode.value__) }

    $cases = @(
        @{ a = '/Realtime.Running'; t = 'Bool' },
        @{ a = '/Realtime.SpindleSpeed:0'; t = 'Double' },
        @{ a = '/Realtime.Cord.Absolute:0'; t = 'Double' },
        @{ a = '/Realtime.PartsCutted'; t = 'Int32' },
        @{ a = '/Static'; t = 'String' },
        @{ a = '/Param:1023:0'; t = 'Int32' },
        @{ a = '/Macro:100'; t = 'Double' },
        @{ a = '/Diagnose:10:0'; t = 'Int32' },
        @{ a = '/SoftPlc:R:100'; t = 'Int32' },
        @{ a = '/Tool.TotalCount'; t = 'Int32' },
        @{ a = '/Alarm.TotalCount'; t = 'Int32' },
        @{ a = '/History.TotalCount'; t = 'Int32' },
        @{ a = '/ToolLife.Current:1'; t = 'String' }
    )
    foreach ($c in $cases) {
        $b = @{ tags = @(@{ address = $c.a; dataType = $c.t }) } | ConvertTo-Json -Depth 5 -Compress
        try {
            $r = Invoke-WebRequest "http://localhost:5173/api/data/$($gsk.id)/read" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($b)) -UseBasicParsing -TimeoutSec 20
            $s = ($r.Content -replace '\s+', ' '); if ($s.Length -gt 260) { $s = $s.Substring(0, 260) }
            W ("  {0,-26} -> HTTP {1}  {2}" -f $c.a, $r.StatusCode, $s)
        } catch {
            $code = $_.Exception.Response.StatusCode.value__; $bb = ''
            try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $bb = $sr.ReadToEnd() } catch { }
            W ("  {0,-26} -> HTTP {1}  {2}" -f $c.a, $code, $bb)
        }
    }
} else { W "未找到 GSK 设备" }

# ---------- 4) 地址空间浏览（前端"点位列表"用） ----------
W ""
W "########## 4) 地址空间（浏览不应触发设备读取）##########"
if ($gsk) {
    foreach ($p in '/Realtime', '/Static', '/Macro', '/Param', '/SoftPlc') {
        try {
            $u = "http://localhost:5173/api/addressspace/$($gsk.id)/children?path=$p"
            $r = Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 10
            $s = ($r.Content -replace '\s+', ' '); if ($s.Length -gt 160) { $s = $s.Substring(0, 160) }
            W ("  {0,-10} -> HTTP {1}  {2}" -f $p, $r.StatusCode, $s)
        } catch { W ("  {0,-10} -> HTTP {1}" -f $p, $_.Exception.Response.StatusCode.value__) }
    }
}

[System.IO.File]::WriteAllLines((Join-Path $out 'report.txt'), $lines, (New-Object System.Text.UTF8Encoding($false)))
"report: $(Join-Path $out 'report.txt')"
