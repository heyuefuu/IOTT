$ErrorActionPreference = 'Continue'
$appcmd = Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe'
function L($m) { "[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $m | Write-Output }

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path $here 'payload\host'
if (-not (Test-Path $payload)) { L "缺少 payload\host（应含 IndustrialIoT.Protocols.Gsk.dll）"; return }
L "Payload: $payload"
Get-ChildItem $payload -File | ForEach-Object { L ("  {0}  {1}KB  SHA256={2}" -f $_.Name, [math]::Round($_.Length / 1KB, 0), (Get-FileHash $_.FullName -Algorithm SHA256).Hash.Substring(0, 16)) }

$bak = "C:\iott-bundle\host\_deploy-bak-$(Get-Date -Format yyyyMMddHHmmss)"
New-Item -ItemType Directory -Force -Path $bak | Out-Null
Get-ChildItem $payload -File | ForEach-Object {
    $t = Join-Path 'C:\iott-bundle\host' $_.Name
    if (Test-Path $t) { Copy-Item $t (Join-Path $bak $_.Name) -Force }
}
L "已备份被覆盖文件 -> $bak"

L "停止 IOTT-Host 站点/应用池"
& $appcmd stop site IOTT-Host 2>&1 | Out-Null
& $appcmd stop apppool IOTT-Host 2>&1 | Out-Null
Start-Sleep -Seconds 4

L "覆盖到 C:\iott-bundle\host"
robocopy $payload 'C:\iott-bundle\host' /E /NFL /NDL /NJH /NJS /NP | Out-Null
L "  robocopy=$LASTEXITCODE"

L "启动"
& $appcmd start apppool IOTT-Host 2>&1 | Out-Null
& $appcmd start site IOTT-Host 2>&1 | Out-Null
Start-Sleep -Seconds 10

# ---- 验证 ----
$dev = $null
try {
    $dev = (Invoke-WebRequest 'http://localhost:5173/api/Devices' -UseBasicParsing -TimeoutSec 20).Content | ConvertFrom-Json |
        Where-Object { $_.protocol -eq 'GskWebServer' -and $_.host -match '^\d{1,3}(\.\d{1,3}){3}$' } |
        Where-Object { $_.host -eq '192.168.1.165' } | Select-Object -First 1
    if (-not $dev) { $dev = (Invoke-WebRequest 'http://localhost:5173/api/Devices' -UseBasicParsing -TimeoutSec 20).Content | ConvertFrom-Json | Where-Object { $_.protocol -eq 'GskWebServer' } | Select-Object -First 1 }
    L "GSK 设备: $($dev.id)  $($dev.host):$($dev.port)  $($dev.name)"
} catch { L "读取设备失败: $($_.Exception.Message)" }

if ($dev) {
    function ReadOne([string]$addr, [string]$type) {
        $b = @{ tags = @(@{ address = $addr; dataType = $type }) } | ConvertTo-Json -Depth 5 -Compress
        try {
            $r = Invoke-WebRequest "http://localhost:5173/api/data/$($dev.id)/read" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($b)) -UseBasicParsing -TimeoutSec 20
            $s = ($r.Content -replace '\s+', ' '); if ($s.Length -gt 300) { $s = $s.Substring(0, 300) }
            L ("  {0,-22} -> HTTP {1}  {2}" -f $addr, $r.StatusCode, $s)
        } catch { L ("  {0,-22} -> HTTP {1}" -f $addr, $_.Exception.Response.StatusCode.value__) }
    }

    L "1) 正常点位（应 Good，且 TotalCount 已修）"
    ReadOne '/Realtime.PartsCutted' 'Int32'
    ReadOne '/Macro:100' 'Double'
    ReadOne '/Tool.TotalCount' 'Int32'
    ReadOne '/Alarm.TotalCount' 'Int32'

    L "2) 裸文件夹地址（应 HTTP 200 + quality=Bad + 带示例提示，不再 502）"
    ReadOne '/Macro' 'Double'
    ReadOne '/SoftPlc' 'Int32'

    L "3) 混批（坏地址 + 好地址：两个都应返回，好的为 Good，不再整批失败）"
    $body = @{ tags = @(@{ address = '/Macro'; dataType = 'Double' }, @{ address = '/Realtime.PartsCutted'; dataType = 'Int32' }) } | ConvertTo-Json -Depth 6 -Compress
    try {
        $r = Invoke-WebRequest "http://localhost:5173/api/data/$($dev.id)/read" -Method Post -ContentType 'application/json' -Body ([Text.Encoding]::UTF8.GetBytes($body)) -UseBasicParsing -TimeoutSec 20
        L ("  mixed -> HTTP {0}  {1}" -f $r.StatusCode, ($r.Content -replace '\s+', ' '))
    } catch { L ("  mixed -> HTTP {0}" -f $_.Exception.Response.StatusCode.value__) }
}
L "完成"
