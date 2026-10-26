#Requires -Version 5.1
<#
.SYNOPSIS
    用 dotnet 并发启动本仓库的 .NET 后端项目（默认 Host + Gateway）。

.DESCRIPTION
    为每个项目启动一个独立的 `dotnet run` 进程（各自新开窗口，便于查看日志）。
    默认先构建再启动；-NoBuild 跳过构建；-Background 隐藏窗口并把输出重定向到
    tmp/dev-run/<项目>.*.log；-Wait 阻塞到所有进程退出。

    本脚本只负责用 dotnet 拉起 .NET 项目，不启动 MySQL / InfluxDB / 前端。
    需要完整环境（含数据库初始化）时请改用 deploy/mysql/Start-Backends.ps1。

.PARAMETER Projects
    要启动的项目，可选 Host、Gateway，默认两个都启动。

.PARAMETER NoBuild
    跳过 dotnet build，直接以 --no-build 运行（需已构建过）。

.PARAMETER Background
    不新开窗口，隐藏运行并把 stdout/stderr 写入 tmp/dev-run。

.PARAMETER Wait
    阻塞等待所有已启动进程退出（Ctrl+C 只结束脚本，不会自动停止子进程）。

.EXAMPLE
    .\run-backends.ps1

.EXAMPLE
    .\run-backends.ps1 -Projects Host -NoBuild

.EXAMPLE
    .\run-backends.ps1 -Background -Wait
#>
[CmdletBinding()]
param(
    [ValidateSet('Host', 'Gateway')]
    [string[]]$Projects = @('Host', 'Gateway'),

    [switch]$NoBuild,

    [switch]$Background,

    [switch]$Wait
)

$ErrorActionPreference = 'Stop'

$registry = [ordered]@{
    Host    = [pscustomobject]@{
        Assembly = 'IndustrialIoT.Host'
        Csproj   = 'Host/src/IndustrialIoT.Host/IndustrialIoT.Host.csproj'
        Urls     = 'http://localhost:5173'
        Port     = 5173
    }
    Gateway = [pscustomobject]@{
        Assembly = 'MachineConnectionApi'
        Csproj   = 'MachineConnectionApi20260423/MachineConnectionApi/MachineConnectionApi/MachineConnectionApi.csproj'
        Urls     = 'http://localhost:5087'
        Port     = 5087
    }
}

$root   = $PSScriptRoot
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
Write-Host "dotnet: $dotnet"

$unknown = $Projects | Where-Object { -not $registry.Contains($_) }
if ($unknown) { throw "未知项目: $($unknown -join ', ')" }

$logDir = Join-Path $root 'tmp/dev-run'
if ($Background -and -not (Test-Path -LiteralPath $logDir)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}

$started = @()
foreach ($key in $Projects) {
    $project = $registry[$key]
    $csproj = Join-Path $root $project.Csproj
    if (-not (Test-Path -LiteralPath $csproj)) {
        throw "[$key] 找不到项目文件: $csproj"
    }
    $projectDir = Split-Path -Path $csproj -Parent

    $existed = Get-NetTCPConnection -State Listen -LocalPort $project.Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($existed) {
        $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($existed.OwningProcess)" -ErrorAction SilentlyContinue
        if ($owner -and $owner.CommandLine -like "*$($project.Assembly)*") {
            Write-Host "[$key] 已在运行 (PID $($existed.OwningProcess))，跳过。" -ForegroundColor Yellow
            $started += [pscustomobject]@{
                Key       = $key
                Assembly  = $project.Assembly
                Url       = $project.Urls
                ProcessId = $existed.OwningProcess
                State     = 'AlreadyRunning'
            }
            continue
        }
        throw "[$key] 端口 $($project.Port) 被非本项目进程占用 (PID $($existed.OwningProcess))；请先释放该端口。"
    }

    if (-not $NoBuild) {
        Write-Host "[$key] 构建 $($project.Assembly) ..." -ForegroundColor Cyan
        & $dotnet build $csproj --nologo --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "[$key] 构建失败，退出码 $LASTEXITCODE" }
    }

    $argumentList = 'run --project "' + $csproj + '" --no-build -- --urls ' +
                    $project.Urls + ' --environment Development'
    Write-Host "[$key] 启动: dotnet $argumentList" -ForegroundColor Green

    if ($Background) {
        $process = Start-Process -FilePath $dotnet -ArgumentList $argumentList `
            -WorkingDirectory $projectDir -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $logDir ($key.ToLowerInvariant() + '.stdout.log')) `
            -RedirectStandardError  (Join-Path $logDir ($key.ToLowerInvariant() + '.stderr.log'))
    }
    else {
        $process = Start-Process -FilePath $dotnet -ArgumentList $argumentList `
            -WorkingDirectory $projectDir -PassThru
    }

    $ready = $false
    $deadline = (Get-Date).AddSeconds(60)
    do {
        if ($process.HasExited) { break }
        if (Get-NetTCPConnection -State Listen -LocalPort $project.Port -ErrorAction SilentlyContinue) { $ready = $true; break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    if ($ready) {
        Write-Host "[$key] 就绪: $($project.Urls)" -ForegroundColor Green
    }
    else {
        Write-Warning "[$key] 60 秒内未监听 $($project.Port)，请检查窗口或日志。"
    }

    $state = if ($process.HasExited) { "Exited($($process.ExitCode))" } elseif ($ready) { 'Started' } else { 'Starting' }
    $started += [pscustomobject]@{
        Key       = $key
        Assembly  = $project.Assembly
        Url       = $project.Urls
        ProcessId = $process.Id
        State     = $state
    }
}

Write-Host ''
Write-Host '已启动:' -ForegroundColor Yellow
$started | Format-Table Key, Assembly, Url, ProcessId, State -AutoSize | Out-String | Write-Host

if ($Background) {
    Write-Host "日志目录: $logDir" -ForegroundColor DarkGray
}

if ($Wait) {
    Write-Host '等待进程退出（Ctrl+C 只结束本脚本，不会自动停止子进程）...' -ForegroundColor Yellow
    $started | ForEach-Object { Wait-Process -Id $_.ProcessId }
    Write-Host '所有进程已退出。'
}
