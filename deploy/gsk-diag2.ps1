$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'gskdiag2'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$lines = New-Object System.Collections.Generic.List[string]
function W($s) { $lines.Add($s) | Out-Null; Write-Host $s }
W "output dir = $out"

function Try-Get([string]$url) {
    try { $r = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 6; return @{ ok = $true; status = [int]$r.StatusCode; body = $r.Content } }
    catch { $c = ''; try { $c = $_.Exception.Response.StatusCode.value__ } catch { }; $b = ''; try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $b = $sr.ReadToEnd() } catch { }; return @{ ok = $false; status = $c; body = $b } }
}

# 1) 找到"可达的机床"设备
$h = (Invoke-WebRequest 'http://localhost:5173/api/Devices' -UseBasicParsing -TimeoutSec 30).Content
[System.IO.File]::WriteAllText("$out\devices.json", $h, (New-Object System.Text.UTF8Encoding($false)))
$hj = $h | ConvertFrom-Json
$gsk = @($hj | Where-Object { $_.protocol -eq 'GskWebServer' })
W "GskWebServer 设备数 = $($gsk.Count)"
$machine = $null
foreach ($d in $gsk) {
    $ipOk = $d.host -match '^\d{1,3}(\.\d{1,3}){3}$'
    $reach = $false
    if ($ipOk) { $probe = Try-Get "http://$($d.host):$($d.port)/api/v1/cnc/mc"; $reach = $probe.ok }
    W ("  {0}  host={1}:{2}  ipOk={3}  /mc可达={4}" -f $d.id, $d.host, $d.port, $ipOk, $reach)
    if (-not $machine -and $reach) { $machine = $d }
}
if (-not $machine) { W "未找到可达的 GskWebServer 机床（/api/v1/cnc/mc 未返回 200）"; [System.IO.File]::WriteAllLines("$out\report.txt", $lines, (New-Object System.Text.UTF8Encoding($false))); "report: $out\report.txt"; return }
W "选用机床: id=$($machine.id) name=$($machine.name) host=$($machine.host):$($machine.port)"
foreach ($pn in $machine.extendedProperties.PSObject.Properties.Name) { W "    ext[$pn]=$($machine.extendedProperties.$pn)" }

# 2) 该机床的采集点位（关键：上次报告缺这个）
W ""
W "=== 该机床的采集点位 (/api/collection-config) ==="
$cfg = $null
try { $cfg = (Invoke-WebRequest "http://localhost:5173/api/collection-config/$($machine.id)" -UseBasicParsing -TimeoutSec 20).Content } catch { W "  读取失败: $($_.Exception.Message)" }
$tags = @()
if ($cfg) {
    [System.IO.File]::WriteAllText("$out\collection-config.json", $cfg, (New-Object System.Text.UTF8Encoding($false)))
    $cj = $cfg | ConvertFrom-Json
    foreach ($p in @($cj)) { foreach ($g in @($p.groups)) { foreach ($t in @($g.tags)) { $tags += $t; W ("  group={0} addr={1} type={2} name={3}" -f $g.groupName, $t.address, $t.dataType, $t.displayName) } } }
}
if ($tags.Count -eq 0) { W "  （该设备没有配置采集点位 profile）" }

# 3) 逐前缀读一遍（映射"哪些前缀在这台机器可用"）
W ""
W "=== 前缀覆盖读取（POST /api/data/{id}/read）==="
$samples = @(
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
    @{ a = '/ToolLife.Current:1'; t = 'String' },
    @{ a = '/Alarm.TotalCount'; t = 'Int32' },
    @{ a = '/History.TotalCount'; t = 'Int32' },
    @{ a = '/Workshop'; t = 'String' }
)
foreach ($tg in $tags) { $samples += @{ a = $tg.address; t = $tg.dataType } }
foreach ($s in $samples) {
    $body = @{ tags = @(@{ address = $s.a; dataType = $s.t }) } | ConvertTo-Json -Depth 5 -Compress
    try {
        $r = Invoke-WebRequest "http://localhost:5173/api/data/$($machine.id)/read" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -UseBasicParsing -TimeoutSec 20
        $snip = ($r.Content -replace '\s+', ' '); if ($snip.Length -gt 220) { $snip = $snip.Substring(0, 220) }
        W ("  {0,-28} -> HTTP {1}  {2}" -f $s.a, $r.StatusCode, $snip)
    } catch {
        $code = $_.Exception.Response.StatusCode.value__; $b = ''
        try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $b = $sr.ReadToEnd() } catch { }
        W ("  {0,-28} -> HTTP {1}  {2}" -f $s.a, $code, $b)
    }
}

# 4) 路径词表（GET；对 200 的再试 POST；保存 200 的响应体）
W ""
W "=== 该固件实际存在的端点（词表探测）==="
$paths = @(
    '/api/v1', '/api/v1/cnc', '/api/v1/cnc/mc', '/api/v1/cnc/mc/gcode',
    '/api/v1/cnc/params', '/api/v1/cnc/param', '/api/v1/cnc/macro', '/api/v1/cnc/macro-values', '/api/v1/cnc/macrovalue',
    '/api/v1/cnc/plc', '/api/v1/cnc/plc/regs', '/api/v1/cnc/plc/reg', '/api/v1/cnc/regs', '/api/v1/cnc/register',
    '/api/v1/cnc/position', '/api/v1/cnc/state', '/api/v1/cnc/status', '/api/v1/cnc/spindle', '/api/v1/cnc/feed',
    '/api/v1/cnc/tool-offsets', '/api/v1/cnc/tool-lifes', '/api/v1/cnc/tool',
    '/api/v1/cnc/info', '/api/v1/cnc/info/diags', '/api/v1/cnc/info/alarms', '/api/v1/cnc/info/history',
    '/api/v1/cnc/alarm', '/api/v1/cnc/alarms', '/api/v1/cnc/diag', '/api/v1/cnc/diags', '/api/v1/cnc/history',
    '/api/v1/cnc/file', '/api/v1/cnc/version', '/api/v1/cnc/system', '/api/v1/cnc/config', '/api/v1/workshop'
)
foreach ($p in $paths) {
    $g = Try-Get "http://$($machine.host):$($machine.port)$p"
    $snip = (($g.body) -replace '\s+', ' '); if ($snip.Length -gt 120) { $snip = $snip.Substring(0, 120) }
    W ("  {0,-32} GET {1}  {2}" -f $p, $g.status, $snip)
    if ($g.ok) {
        $safe = ($p -replace '[/:]', '_'); [System.IO.File]::WriteAllText("$out\body$safe.json", $g.body, (New-Object System.Text.UTF8Encoding($false)))
        try { $pr = Invoke-WebRequest "http://$($machine.host):$($machine.port)$p" -Method Post -ContentType 'application/json' -Body '{}' -UseBasicParsing -TimeoutSec 6; W ("      POST -> {0}" -f $pr.StatusCode) }
        catch { W ("      POST -> {0}" -f $_.Exception.Response.StatusCode.value__) }
    }
}

[System.IO.File]::WriteAllLines("$out\report.txt", $lines, (New-Object System.Text.UTF8Encoding($false)))
"report: $out\report.txt"
Get-ChildItem $out -File | ForEach-Object { "  $($_.Name)  $($_.Length)B" }
