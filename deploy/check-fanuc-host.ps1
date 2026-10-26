$ErrorActionPreference = 'Continue'
$out = 'D:\fanucdiag'
try { New-Item -ItemType Directory -Force -Path $out -ErrorAction Stop | Out-Null } catch { $out = Join-Path $env:TEMP 'fanucdiag'; New-Item -ItemType Directory -Force -Path $out | Out-Null }
$lines = New-Object System.Collections.Generic.List[string]
function W($s) { $lines.Add([string]$s) | Out-Null; Write-Host $s }
W "output dir = $out"
W ("now = " + (Get-Date -Format o))

$H = 'http://localhost:5173'
$G = 'http://127.0.0.1:5087'

# 1) Host 目录里的 FANUC 原生库
W ""
W "=== 1) C:\iott-bundle\host 下的 FWLIB 原生库 ==="
Get-ChildItem 'C:\iott-bundle\host' -Filter '*wlib*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
    $vi = (Get-Item $_.FullName).VersionInfo
    W ("  {0,-18} {1,8}B  ver={2}  desc={3}" -f $_.Name, $_.Length, $vi.FileVersion, $vi.FileDescription)
}
if (-not (Test-Path 'C:\iott-bundle\host\Fwlib64.dll')) { W "  !! 缺少 Fwlib64.dll —— FOCAS 驱动无法工作" }

# 2) Host 侧 FOCAS 设备
W ""
W "=== 2) Host 侧 FOCAS 设备 ==="
$hostDevs = $null
try { $hostDevs = (Invoke-WebRequest "$H/api/Devices" -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json } catch { W "  Host 设备读取失败" }
$hostFanuc = @()
if ($hostDevs) {
    $hostFanuc = @($hostDevs | Where-Object { $_.protocol -eq 'FOCAS' })
    W ("  Host 设备总数={0}  FOCAS={1}" -f @($hostDevs).Count, $hostFanuc.Count)
    foreach ($d in $hostFanuc) {
        W ("    id={0} name=[{1}] {2}:{3} status={4} ext={5}" -f $d.id, $d.name, $d.host, $d.port, $d.status, ($d.extendedProperties | ConvertTo-Json -Compress))
    }
}

# 3) 网关侧 FOCAS 设备
W ""
W "=== 3) 网关侧 FOCAS 设备 ==="
$login = $null
try { $login = Invoke-RestMethod -Method Post "$G/api/system/auth/login" -ContentType 'application/json' -Body '{"username":"admin","password":"admin@123"}' -TimeoutSec 20 } catch { }
$hdr = @{ 'X-Auth-Token' = $login.token }
$gwFanuc = @()
if ($login) {
    try {
        $gwDevs = (Invoke-WebRequest "$G/api/devices" -Headers $hdr -UseBasicParsing -TimeoutSec 30).Content | ConvertFrom-Json
        $gwFanuc = @($gwDevs | Where-Object { $_.protocol -eq 'FOCAS' })
        W ("  网关设备总数={0}  FOCAS={1}" -f @($gwDevs).Count, $gwFanuc.Count)
        foreach ($d in $gwFanuc) {
            $inHost = @($hostFanuc | Where-Object { $_.id -eq $d.id }).Count
            W ("    id={0} name=[{1}] {2}:{3} upstreamSynced={4} 在Host={5}" -f $d.id, $d.name, $d.host, $d.port, $d.upstreamSynced, $inHost)
        }
    } catch { W "  网关设备读取失败" }
}

# 4) 网关的设备连接测试（这就是报"仅 TCP 端口可达"的地方）
W ""
W "=== 4) 网关 test-connection（看 mode/reason/errorMessage）==="
if ($login) {
    foreach ($d in $gwFanuc) {
        try {
            $r = Invoke-WebRequest "$G/api/devices/$($d.id)/test-connection" -Method Post -Headers $hdr -UseBasicParsing -TimeoutSec 30
            W ("  {0} [{1}:{2}] -> {3}" -f $d.name, $d.host, $d.port, ($r.Content -replace '\s+', ' '))
        } catch { W ("  {0} -> HTTP {1}" -f $d.name, $_.Exception.Response.StatusCode.value__) }
        Start-Sleep -Milliseconds 500
    }
}

# 5) Host 日志里的 FOCAS 相关
W ""
W "=== 5) Host 日志 FOCAS 相关（最近 20 条）==="
$log = Get-ChildItem 'C:\iott-bundle\host\logs' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) {
    W "  文件: $($log.Name)"
    Select-String -Path $log.FullName -Pattern 'FOCAS|Focas|fwlib|Fwlib|DllNotFound|BadImageFormat|8193|Unable to load' -ErrorAction SilentlyContinue | Select-Object -Last 20 | ForEach-Object { W ("    " + $_.Line) }
} else { W "  未找到日志" }

# 6) 从目标机直接用 FOCAS 驱动探测（样例程序能读到数据的这台）
W ""
W "=== 6) 用 FocasProbe 直连（与样例同口径）==="
$probe = $null
foreach ($p in 'D:\focas-probe\FocasProbe.exe', 'C:\focas-probe\FocasProbe.exe') { if (Test-Path $p) { $probe = $p; break } }
if (-not $probe) { foreach ($drv in 'D','E','F','G') { $p = "${drv}:\focas-probe\FocasProbe.exe"; if (Test-Path $p) { $probe = $p; break } } }
if ($probe -and $gwFanuc.Count -gt 0) {
    W "  使用: $probe"
    foreach ($d in $gwFanuc) {
        W ("  -- {0} {1}:{2} --" -f $d.name, $d.host, $d.port)
        & $probe $d.host $d.port 10 2>&1 | ForEach-Object { W ("     " + $_) }
    }
} else { W "  未找到 FocasProbe.exe（应在 <盘>:\focas-probe\）或没有 FOCAS 设备" }

[System.IO.File]::WriteAllLines((Join-Path $out 'report.txt'), $lines, (New-Object System.Text.UTF8Encoding($false)))
"report: $(Join-Path $out 'report.txt')"
