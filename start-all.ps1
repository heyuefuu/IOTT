#Requires -Version 5.1
<#
.SYNOPSIS
    一站式脚本：检查 -> 修正 -> 启动 MySQL(3307) + Host(5173) + Gateway(5087) + 前端 Vite(4171)。

.DESCRIPTION
    执行顺序：
      1. 检查四个服务的当前状态并打印表格；
      2. 修正 MySQL：deploy/mysql/Start-MySql.ps1（自动探测 bin、建库建账号、
         修正两个 appsettings.Local.json 为 127.0.0.1:3307）+ Import-InitialData.ps1（有表则跳过）；
      3. 修正前端依赖：node_modules 缺失时自动 npm ci（无锁文件则 npm install）；
      4. -Force 时先结束已在运行的本项目进程；
      5. 启动后端 run-backends.ps1（已运行跳过、端口被占报错）+ 启动前端 Vite；
      6. 再次打印最终状态。

.PARAMETER Projects
    要处理的后端，可选 Host、Gateway，默认两个都处理。

.PARAMETER CheckOnly
    只检查并打印状态，不做任何修改或启动。

.PARAMETER SkipMySql
    跳过 MySQL 修正与种子导入。

.PARAMETER SkipFrontend
    跳过前端（依赖安装 / 启动 / 状态）。

.PARAMETER Force
    对已在运行的本项目服务先结束再重启。

.PARAMETER NoBuild
    启动后端前跳过 dotnet build。

.PARAMETER Background
    隐藏窗口运行，后端输出到 tmp/dev-run/*.log，前端输出到 tmp/dev-run/frontend.*.log。

.PARAMETER Wait
    阻塞等待后端进程退出（前端在独立窗口/后台运行，不随本参数阻塞）。

.EXAMPLE
    .\start-all.ps1
.EXAMPLE
    .\start-all.ps1 -CheckOnly
.EXAMPLE
    .\start-all.ps1 -SkipFrontend -SkipMySql
.EXAMPLE
    .\start-all.ps1 -Force -Background
#>
[CmdletBinding()]
param(
    [ValidateSet('Host', 'Gateway')]
    [string[]]$Projects = @('Host', 'Gateway'),

    [switch]$CheckOnly,
    [switch]$SkipMySql,
    [switch]$SkipFrontend,
    [switch]$Force,
    [switch]$NoBuild,
    [switch]$Background,
    [switch]$Wait
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$runtime = Join-Path $root 'deploy/mysql/.runtime'
$logDir = Join-Path $root 'tmp/dev-run'

$backends = [ordered]@{
    Host    = [pscustomobject]@{ Name = 'Host';    Port = 5173; Assembly = 'IndustrialIoT.Host';    HealthUrl = 'http://localhost:5173/api/Devices' }
    Gateway = [pscustomobject]@{ Name = 'Gateway'; Port = 5087; Assembly = 'MachineConnectionApi'; HealthUrl = 'http://localhost:5087/swagger/v1/swagger.json' }
}
$frontend = [pscustomobject]@{
    Name      = 'Frontend'
    Port      = 4171
    Directory = 'code20260423/code'
    HealthUrl = 'http://localhost:4171/'
}

function Get-MySqlStatus {
    $listener = Get-NetTCPConnection -State Listen -LocalPort 3307 -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) {
        return [pscustomobject]@{ Name = 'MySQL :3307'; State = 'Stopped'; Detail = '未监听' }
    }

    $statePath = Join-Path $runtime 'instance.json'
    if (-not (Test-Path -LiteralPath $statePath)) {
        return [pscustomobject]@{ Name = 'MySQL :3307'; State = 'Occupied'; Detail = "非项目实例 (PID $($listener.OwningProcess))" }
    }

    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    $client = Join-Path $state.Bin 'mysql.exe'
    $clientConfig = Join-Path $runtime 'client.cnf'
    $dataDir = [IO.Path]::GetFullPath((Join-Path $runtime 'data')).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $client) -or -not (Test-Path -LiteralPath $clientConfig)) {
        return [pscustomobject]@{ Name = 'MySQL :3307'; State = 'Running'; Detail = '端口在监听（未校验数据目录）' }
    }

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $actual = & $client "--defaults-extra-file=$clientConfig" --connect-timeout=2 --batch --skip-column-names '--execute=SELECT @@datadir' 2>$null
    $ok = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previous
    if (-not $ok) {
        return [pscustomobject]@{ Name = 'MySQL :3307'; State = 'Degraded'; Detail = '端口在监听但凭据校验失败' }
    }

    $actual = [IO.Path]::GetFullPath($actual.Trim()).TrimEnd('\', '/')
    if ($actual -ieq $dataDir) {
        return [pscustomobject]@{ Name = 'MySQL :3307'; State = 'Running'; Detail = '项目实例已就绪' }
    }
    return [pscustomobject]@{ Name = 'MySQL :3307'; State = 'Occupied'; Detail = "数据目录不符: $actual" }
}

function Get-BackendStatus([pscustomobject]$backend) {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $backend.Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) {
        return [pscustomobject]@{ Name = "$($backend.Name) :$($backend.Port)"; State = 'Stopped'; Detail = '未监听' }
    }

    $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
    $isOurs = $owner -and $owner.CommandLine -like "*$($backend.Assembly)*"

    $healthy = $false
    try {
        $response = Invoke-WebRequest -Uri $backend.HealthUrl -TimeoutSec 5 -UseBasicParsing
        $healthy = $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
    }
    catch {
        if ($_.Exception.Response) { $healthy = $true }
    }

    if ($isOurs -and $healthy) {
        return [pscustomobject]@{ Name = "$($backend.Name) :$($backend.Port)"; State = 'Running'; Detail = "PID $($listener.OwningProcess)" }
    }
    if ($isOurs) {
        return [pscustomobject]@{ Name = "$($backend.Name) :$($backend.Port)"; State = 'Degraded'; Detail = "PID $($listener.OwningProcess) 无 HTTP 响应" }
    }
    return [pscustomobject]@{ Name = "$($backend.Name) :$($backend.Port)"; State = 'Occupied'; Detail = "非本项目进程 (PID $($listener.OwningProcess))" }
}

function Test-FrontendDeps {
    return Test-Path -LiteralPath (Join-Path (Join-Path $root $frontend.Directory) 'node_modules')
}

function Get-FrontendStatus {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $frontend.Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $listener) {
        $detail = if (Test-FrontendDeps) { '未监听' } else { '未监听（node_modules 缺失）' }
        return [pscustomobject]@{ Name = "Frontend :$($frontend.Port)"; State = 'Stopped'; Detail = $detail }
    }

    $healthy = $false
    try {
        $response = Invoke-WebRequest -Uri $frontend.HealthUrl -TimeoutSec 5 -UseBasicParsing
        $healthy = $response.StatusCode -ge 200 -and $response.StatusCode -lt 500
    }
    catch {
        if ($_.Exception.Response) { $healthy = $true }
    }

    if ($healthy) {
        return [pscustomobject]@{ Name = "Frontend :$($frontend.Port)"; State = 'Running'; Detail = "PID $($listener.OwningProcess)" }
    }
    return [pscustomobject]@{ Name = "Frontend :$($frontend.Port)"; State = 'Degraded'; Detail = "PID $($listener.OwningProcess) 无 HTTP 响应" }
}

function Show-Status {
    $rows = @((Get-MySqlStatus)) + ($Projects | ForEach-Object { Get-BackendStatus $backends[$_] })
    if (-not $SkipFrontend) { $rows += Get-FrontendStatus }
    $rows | Format-Table -AutoSize | Out-String | Write-Host
}

function Get-NpmCommand([string]$name) {
    $cmd = Get-Command "$name.cmd" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return (Get-Command $name -ErrorAction Stop).Source
}

Write-Host '==== 服务状态 ====' -ForegroundColor Cyan
Show-Status

if ($CheckOnly) {
    Write-Host '仅检查模式（-CheckOnly），未做任何修改。' -ForegroundColor DarkGray
    return
}

# 若 3307 已在监听且本项目已存在 appsettings.Local.json，说明可直接复用已有实例，
# 跳过实例修正（避免 Start-MySql.ps1 因"端口被占用"直接抛错，导致后端拿不到本地配置而启动失败）。
$hostLocal = Join-Path $root 'Host/src/IndustrialIoT.Host/appsettings.Local.json'
$gwLocal   = Join-Path $root 'MachineConnectionApi20260423/MachineConnectionApi/MachineConnectionApi/appsettings.Local.json'
$mySqlListening = [bool](Get-NetTCPConnection -State Listen -LocalPort 3307 -ErrorAction SilentlyContinue | Select-Object -First 1)
$mySqlReuse = $mySqlListening -and (Test-Path -LiteralPath $hostLocal) -and (Test-Path -LiteralPath $gwLocal)

if (-not $SkipMySql -and $mySqlReuse) {
    Write-Host '==== 检测到 MySQL(3307) 已在运行且本地配置存在，复用实例（跳过修正）====' -ForegroundColor Yellow
}
elseif (-not $SkipMySql) {
    Write-Host '==== 修正 MySQL（实例 / 库 / 账号 / appsettings.Local.json / 种子）====' -ForegroundColor Cyan
    & (Join-Path $root 'deploy/mysql/Start-MySql.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Start-MySql.ps1 失败。' }
    & (Join-Path $root 'deploy/mysql/Import-InitialData.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'Import-InitialData.ps1 失败。' }
}

$frontendDir = Join-Path $root $frontend.Directory
if (-not $SkipFrontend -and -not (Test-FrontendDeps)) {
    Write-Host '==== 修正前端依赖（首次安装）====' -ForegroundColor Cyan
    $npm = Get-NpmCommand 'npm'
    $useCi = Test-Path -LiteralPath (Join-Path $frontendDir 'package-lock.json')
    Push-Location $frontendDir
    try {
        if ($useCi) {
            & $npm ci
            if ($LASTEXITCODE -ne 0) {
                Write-Warning 'npm ci 失败，改用 npm install。'
                & $npm install
            }
        }
        else {
            & $npm install
        }
        if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败。' }
    }
    finally { Pop-Location }
}

if ($Force) {
    foreach ($key in $Projects) {
        $backend = $backends[$key]
        $listener = Get-NetTCPConnection -State Listen -LocalPort $backend.Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $listener) { continue }
        $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)" -ErrorAction SilentlyContinue
        if ($owner -and $owner.CommandLine -like "*$($backend.Assembly)*") {
            Write-Host "[$key] 强制重启：结束 PID $($listener.OwningProcess)" -ForegroundColor Yellow
            & taskkill /PID $listener.OwningProcess /T /F | Out-Null
            Start-Sleep -Seconds 1
        }
    }
    if (-not $SkipFrontend) {
        $listener = Get-NetTCPConnection -State Listen -LocalPort $frontend.Port -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($listener) {
            Write-Host "[Frontend] 强制重启：结束 PID $($listener.OwningProcess)" -ForegroundColor Yellow
            & taskkill /PID $listener.OwningProcess /T /F | Out-Null
            Start-Sleep -Seconds 1
        }
    }
}

Write-Host '==== 启动后端 ====' -ForegroundColor Cyan
& (Join-Path $root 'run-backends.ps1') -Projects $Projects -NoBuild:$NoBuild -Background:$Background -Wait:$Wait

if (-not $SkipFrontend) {
    Write-Host '==== 启动前端 Vite ====' -ForegroundColor Cyan
    $listener = Get-NetTCPConnection -State Listen -LocalPort $frontend.Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        Write-Host "[Frontend] 已在运行 (PID $($listener.OwningProcess))，跳过。" -ForegroundColor Yellow
    }
    else {
        if ($Background -and -not (Test-Path -LiteralPath $logDir)) {
            New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        }
        # 优先直接运行本地 vite.js（单进程、无 npx/conhost 包装）；缺失时回退 npx。
        $viteEntry = Join-Path $frontendDir 'node_modules/vite/bin/vite.js'
        if (Test-Path -LiteralPath $viteEntry) {
            $exe = (Get-Command node -ErrorAction Stop).Source
            $arguments = @('"' + $viteEntry + '"')
        }
        else {
            $exe = Get-NpmCommand 'npx'
            $arguments = @('vite')
        }
        if ($Background) {
            $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $frontendDir `
                -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $logDir 'frontend.stdout.log') `
                -RedirectStandardError  (Join-Path $logDir 'frontend.stderr.log')
        }
        else {
            $process = Start-Process -FilePath $exe -ArgumentList $arguments -WorkingDirectory $frontendDir -PassThru
        }

        $ready = $false
        $deadline = (Get-Date).AddSeconds(90)
        do {
            if ($process.HasExited) { break }
            if (Get-NetTCPConnection -State Listen -LocalPort $frontend.Port -ErrorAction SilentlyContinue) { $ready = $true; break }
            Start-Sleep -Milliseconds 500
        } while ((Get-Date) -lt $deadline)
        if ($ready) {
            Write-Host "[Frontend] 就绪: http://localhost:$($frontend.Port)" -ForegroundColor Green
        }
        else {
            Write-Warning "[Frontend] 90 秒内未监听 $($frontend.Port)，请检查窗口或 tmp/dev-run/frontend.*.log。"
        }
    }
}

Write-Host '==== 最终状态 ====' -ForegroundColor Cyan
Show-Status
