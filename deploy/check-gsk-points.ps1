$ErrorActionPreference = 'Continue'
$out = 'D:\gskpoints'
try { New-Item -ItemType Directory -Force -Path $out -ErrorAction Stop | Out-Null } catch { $out = Join-Path $env:TEMP 'gskpoints'; New-Item -ItemType Directory -Force -Path $out | Out-Null }
$lines = New-Object System.Collections.Generic.List[string]
function W($s) { $lines.Add([string]$s) | Out-Null; Write-Host $s }
W "output dir = $out"
W ("now = " + (Get-Date -Format o))

$H = 'http://localhost:5173'
$G = 'http://127.0.0.1:5087'

# 注意：不要用 @( ... | ConvertFrom-Json )，会把整个数组包成 1 个元素
$devs = (Invoke-WebRequest "$H/api/Devices" -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
$devList = @($devs)
W ("Host 设备数 = " + $devList.Count)

function ProfileInfo([string]$deviceId) {
    $cc = $null
    try { $cc = (Invoke-WebRequest "$H/api/collection-config/$deviceId" -UseBasicParsing -TimeoutSec 15).Content } catch { return @{ count = -1; text = '读取失败' } }
    if (-not $cc -or $cc -eq '[]') { return @{ count = 0; text = '' } }
    $n = 0; $addr = New-Object System.Collections.Generic.List[string]
    try {
        $profiles = @($cc | ConvertFrom-Json)
        foreach ($p in $profiles) { foreach ($g in @($p.groups)) { foreach ($t in @($g.tags)) { $n++; if ($addr.Count -lt 6) { $addr.Add([string]$t.address) } } } }
    } catch { }
    return @{ count = $n; text = ($addr -join ', ') }
}

W ""
W "=== 1) GSK 设备（Host）与各自点位 ==="
$gsk = @($devList | Where-Object { $_.protocol -eq 'GskWebServer' })
W ("GSK 设备数 = " + $gsk.Count)
foreach ($d in $gsk) {
    $pi = ProfileInfo $d.id
    W ("  id={0}" -f $d.id)
    W ("     name=[{0}]  {1}:{2}  status={3}" -f $d.name, $d.host, $d.port, $d.status)
    W ("     ext={0}" -f ($d.extendedProperties | ConvertTo-Json -Compress))
    W ("     profile 点位数={0}  示例={1}" -f $pi.count, $pi.text)
}

W ""
W "=== 2) 所有有点位的设备 ==="
$any = 0
foreach ($d in $devList) {
    $pi = ProfileInfo $d.id
    if ($pi.count -gt 0) { $any++; W ("  [{0}] id={1}  {2}:{3}  name=[{4}]  点位={5}  示例={6}" -f $d.protocol, $d.id, $d.host, $d.port, $d.name, $pi.count, $pi.text) }
}
if ($any -eq 0) { W "  （没有任何设备配置了点位）" }

W ""
W "=== 3) 验证任务 <-> 设备 <-> 点位 ==="
$login = Invoke-RestMethod -Method Post "$G/api/system/auth/login" -ContentType 'application/json' -Body '{"username":"admin","password":"admin@123"}' -TimeoutSec 20
$hdr = @{ 'X-Auth-Token' = $login.token }
$tasks = (Invoke-WebRequest "$G/api/verify/tasks" -Headers $hdr -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
foreach ($t in @($tasks)) {
    $d = $devList | Where-Object { $_.id -eq $t.deviceId } | Select-Object -First 1
    $pi = ProfileInfo $t.deviceId
    W ("  TASK {0}  name=[{1}]  result={2}  detail={3}" -f $t.id, $t.name, $t.result, $t.detail)
    W ("     deviceId={0}  -> Host: {1}   该 id 点位数={2}" -f $t.deviceId, $(if ($d) { "[" + $d.protocol + "] " + $d.name + " " + $d.host + ":" + $d.port } else { "<Host 未找到>" }), $pi.count)
    W ("     concurrentDeviceIds=[{0}]" -f ((@($t.options.concurrentDeviceIds) -join ',')))
    W ("     transferDeviceIds=[{0}]" -f ((@($t.options.transferDeviceIds) -join ',')))
}

W ""
W "=== 4) 网关侧设备（看同步状态）==="
$gwDevs = (Invoke-WebRequest "$G/api/devices" -Headers $hdr -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
foreach ($d in @($gwDevs | Where-Object { $_.protocol -eq 'GskWebServer' })) {
    W ("  GW id={0}  name=[{1}]  {2}:{3}  upstreamSynced={4}" -f $d.id, $d.name, $d.host, $d.port, $d.upstreamSynced)
}

W ""
W "=== 5) 模拟器 GSK 直读自检 ==="
$simGsk = @($gsk | Where-Object { $_.host -eq '192.168.1.50' }) | Select-Object -First 1
if (-not $simGsk) { $simGsk = $gsk | Select-Object -First 1 }
if ($simGsk) {
    W ("  用设备: {0}  [{1}]  {2}:{3}" -f $simGsk.id, $simGsk.name, $simGsk.host, $simGsk.port)
    $cases = @(
        @{ a = '/Realtime.Running'; t = 'Bool' },
        @{ a = '/Realtime.PartsCutted'; t = 'Int32' },
        @{ a = '/Static'; t = 'String' },
        @{ a = '/Macro:100'; t = 'Double' }
    )
    foreach ($c in $cases) {
        $b = @{ tags = @(@{ address = $c.a; dataType = $c.t }) } | ConvertTo-Json -Depth 5 -Compress
        try {
            $r = Invoke-WebRequest "$H/api/data/$($simGsk.id)/read" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($b)) -UseBasicParsing -TimeoutSec 20
            $s = ($r.Content -replace '\s+', ' '); if ($s.Length -gt 200) { $s = $s.Substring(0, 200) }
            W ("    {0,-24} -> HTTP {1}  {2}" -f $c.a, $r.StatusCode, $s)
        } catch { W ("    {0,-24} -> HTTP {1}" -f $c.a, $_.Exception.Response.StatusCode.value__) }
    }
}

[System.IO.File]::WriteAllLines((Join-Path $out 'report.txt'), $lines, (New-Object System.Text.UTF8Encoding($false)))
"report: $(Join-Path $out 'report.txt')"
