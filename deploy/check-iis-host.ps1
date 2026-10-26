$ErrorActionPreference = 'Continue'
$appcmd = Join-Path $env:WINDIR 'System32\inetsrv\appcmd.exe'
function L($m) { "[{0}] {1}" -f (Get-Date -Format HH:mm:ss), $m | Write-Output }
L "本机 IPv4: " + ((Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -like '192.168.*' } | ForEach-Object { "$($_.InterfaceAlias)=$($_.IPAddress)" }) -join ', ')

L "=== 站点 ==="
& $appcmd list sites 2>&1 | ForEach-Object { "  $_" }
L "=== 应用池 ==="
& $appcmd list apppools 2>&1 | ForEach-Object { "  $_" }

L "=== 端口监听 ==="
& cmd /c "netstat -ano" 2>&1 | Select-String 'LISTENING' | Select-String ':5173|:5087|:8082|:3307' | ForEach-Object { "  $($_.Line.Trim())" }

L "=== 启动 IOTT-Host（站点+池）==="
& $appcmd start apppool /apppool.name:IOTT-Host 2>&1 | ForEach-Object { "  $_" }
& $appcmd start site /site.name:IOTT-Host 2>&1 | ForEach-Object { "  $_" }
Start-Sleep -Seconds 8
& cmd /c "netstat -ano" 2>&1 | Select-String 'LISTENING' | Select-String ':5173' | ForEach-Object { "  5173 -> $($_.Line.Trim())" }

L "=== Host API 自检 ==="
try { $n = ((Invoke-WebRequest 'http://127.0.0.1:5173/api/Devices' -UseBasicParsing -TimeoutSec 20).Content | ConvertFrom-Json).Count; L "  /api/Devices -> $n 台" } catch { L "  /api/Devices 失败: $($_.Exception.Message)" }
try { L ("  /api/collection/mqtt/status -> " + (Invoke-WebRequest 'http://127.0.0.1:5173/api/collection/mqtt/status' -UseBasicParsing -TimeoutSec 15).Content) } catch { L "  mqtt/status 失败" }

L "=== Host 日志尾部（找停摆原因）==="
$log = Get-ChildItem 'C:\iott-bundle\host\logs' -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if ($log) { L "  文件: $($log.Name)"; Get-Content $log.FullName -Tail 25 -Encoding UTF8 | ForEach-Object { "    $_" } } else { L "  无日志" }
