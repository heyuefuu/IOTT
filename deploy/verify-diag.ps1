$ErrorActionPreference = 'Continue'
$out = 'D:\verifydiag'
try { New-Item -ItemType Directory -Force -Path $out -ErrorAction Stop | Out-Null } catch { $out = Join-Path $env:TEMP 'verifydiag'; New-Item -ItemType Directory -Force -Path $out | Out-Null }
$lines = New-Object System.Collections.Generic.List[string]
function W($s) { $lines.Add([string]$s) | Out-Null; Write-Host $s }
W "output dir = $out"

$gw = 'http://127.0.0.1:5087'
$login = Invoke-RestMethod -Method Post "$gw/api/system/auth/login" -ContentType 'application/json' -Body '{"username":"admin","password":"admin@123"}' -TimeoutSec 20
$hdr = @{ 'X-Auth-Token' = $login.token }

# 设备表（Host + 网关），用于核对任务设备
$hostDevices = @{}
try { (Invoke-WebRequest 'http://localhost:5173/api/Devices' -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json | ForEach-Object { $hostDevices[$_.id] = $_ } } catch { W "Host 设备读取失败" }
$gwDevices = @{}
try { (Invoke-WebRequest "$gw/api/devices" -Headers $hdr -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json | ForEach-Object { $gwDevices[$_.id] = $_ } } catch { W "网关设备读取失败" }

# 评价规则里各项的 test 配置（读取地址/时长等）
$rules = @{}
try {
    $cfg = Get-Content 'C:\iott-bundle\gateway\App_Data\evaluation-indicators.json' -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($c in @($cfg)) { foreach ($s in @($c.indicators)) { foreach ($ch in @($s.children)) { foreach ($it in @($ch.items)) { if ($it.metricId) { $rules[$it.metricId] = $it } } } } }
} catch { W "读取 evaluation-indicators.json 失败: $($_.Exception.Message)" }
W ""
W "=== 评价规则（machine 类）关键项 ==="
foreach ($k in $rules.Keys) {
    $it = $rules[$k]; $t = $it.automation.test
    W ("  {0,-24} unit={1} test.addr='{2}' type={3} dur={4}m interval={5}s dir='{6}' max={7}" -f $k, $it.automation.unit, $t.readAddress, $t.readDataType, $t.durationMinutes, $t.sampleIntervalSeconds, $t.targetDirectory, $t.maxConnections)
}

# 验证任务
$tasks = (Invoke-WebRequest "$gw/api/verify/tasks" -Headers $hdr -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
W ""
W "=== 验证任务 ==="
foreach ($t in @($tasks)) {
    W ("  {0}  '{1}'  status={2} result={3} detail={4}" -f $t.id, $t.name, $t.status, $t.result, $t.detail)
    W ("     deviceId={0}  category={1}  metrics={2}" -f $t.deviceId, $t.evaluationCategory, (@($t.metricIds) -join ','))
    if ($t.options) { W ("     options=" + ($t.options | ConvertTo-Json -Compress)) }
    $d = $hostDevices[$t.deviceId]
    if ($d) { W ("     设备(Host): [{0}] {1}:{2}  status={3}  transfer={4}" -f $d.protocol, $d.host, $d.port, $d.status, $(if ($d.transfer) { $d.transfer.protocol + '://' + $d.transfer.host + ':' + $d.transfer.port } else { '<无>' })) }
    else { W "     设备(Host): 未找到（可能未同步）" }
    $gd = $gwDevices[$t.deviceId]
    if ($gd) { W ("     设备(GW)  : [{0}] {1}:{2}  ext={3}" -f $gd.protocol, $gd.host, $gd.port, ($gd.extendedProperties | ConvertTo-Json -Compress)) }

    # 该设备是否有采集点位（决定"未配置读取地址"类未评分）
    try {
        $cc = (Invoke-WebRequest "http://localhost:5173/api/collection-config/$($t.deviceId)" -UseBasicParsing -TimeoutSec 15).Content
        if ($cc -and $cc -ne '[]') { W ("     采集点位(profile): " + (($cc -replace '\s+', ' ').Substring(0, [Math]::Min(200, ($cc -replace '\s+', ' ').Length)))) } else { W "     采集点位(profile): 无" }
    } catch { W "     采集点位读取失败" }

    if ($t.lastRunJson) {
        $r = $t.lastRunJson | ConvertFrom-Json
        W ("     lastRun: status={0} result={1} detail={2}" -f $r.status, $r.result, $r.detail)
        foreach ($m in @($r.metrics)) {
            W ("      - [{0}] status={1}/{2} exec={3} src={4} value='{5}' score={6}" -f $m.metricId, $m.status, $m.result, $m.executionStatus, $m.measurementSource, $m.value, $m.score)
            W ("          reason: {0}" -f $m.scoreReason)
            foreach ($e in (@($m.evidence) | Select-Object -Last 3)) { W ("          ev: $e") }
        }
    }
}

[System.IO.File]::WriteAllLines((Join-Path $out 'report.txt'), $lines, (New-Object System.Text.UTF8Encoding($false)))
"report: $(Join-Path $out 'report.txt')"
