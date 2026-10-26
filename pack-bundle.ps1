#Requires -Version 5.1
<#
.SYNOPSIS
   打包：前端构建 + Host/Gateway 自包含发布 + 便携 Node + 脚本/数据，产出 artifacts\iott-bundle（独立部署，不依赖 IIS）。
.DESCRIPTION
   产物结构：
     iott-bundle\
       host\             Host 自包含发布（.exe 单机可跑）
       gateway\          Gateway 自包含发布
       frontend\dist\    前端构建产物
       frontend\serve.mjs  纯 Node 静态托管 + 反代（用包内 node 运行）
       node\node.exe     便携 Node（目标机无需安装）
       deploy\mysql\...  MySQL 脚本与种子（不含 .runtime 数据）
       probe-env.ps1 / standalone-deploy.ps1   目标机脚本
       manifest.json
.EXAMPLE
   .\pack-bundle.ps1
.EXAMPLE
   .\pack-bundle.ps1 -Clean -NodeExe "C:\Program Files\nodejs\node.exe"
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$Output = 'artifacts\iott-bundle',
    [string]$NodeExe = '',
    [switch]$SkipFrontendBuild,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root $Output
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Log([string]$m) { Write-Host "[pack] $m" -ForegroundColor Cyan }
function Run([string]$file, [string[]]$arguments) {
    & $file @arguments
    if ($LASTEXITCODE -ne 0) { throw "$file $($arguments -join ' ') 失败，退出码 $LASTEXITCODE" }
}

if ($Clean -and (Test-Path -LiteralPath $out)) { Remove-Item -LiteralPath $out -Recurse -Force }
[void][System.IO.Directory]::CreateDirectory($out)

# 1) 前端构建
$fe = Join-Path $root 'code20260423\code'
$npm = (Get-Command npm.cmd -ErrorAction SilentlyContinue)
if (-not $npm) { $npm = Get-Command npm -ErrorAction Stop }
$npmExe = $npm.Source
if (-not $SkipFrontendBuild) {
    if (-not (Test-Path -LiteralPath (Join-Path $fe 'node_modules'))) {
        Log '安装前端依赖…'
        if (Test-Path -LiteralPath (Join-Path $fe 'package-lock.json')) { & $npmExe ci } else { & $npmExe install }
        if ($LASTEXITCODE -ne 0) { & $npmExe install }
        if ($LASTEXITCODE -ne 0) { throw '前端依赖安装失败' }
    }
    Log '前端构建 (npm run build)…'
    Push-Location $fe
    try { & $npmExe run build; if ($LASTEXITCODE -ne 0) { throw '前端构建失败' } } finally { Pop-Location }
}

# 2) 发布后端（自包含 win-x64）
$hostProj = Join-Path $root 'Host\src\IndustrialIoT.Host\IndustrialIoT.Host.csproj'
$gwProj = Join-Path $root 'MachineConnectionApi20260423\MachineConnectionApi\MachineConnectionApi\MachineConnectionApi.csproj'
if (-not (Test-Path -LiteralPath $hostProj)) { throw "未找到 $hostProj" }
if (-not (Test-Path -LiteralPath $gwProj)) { throw "未找到 $gwProj" }
Log '发布 Host（自包含）…'
Run 'dotnet' @('publish', $hostProj, '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', (Join-Path $out 'host'))
Log '发布 Gateway（自包含）…'
Run 'dotnet' @('publish', $gwProj, '-c', $Configuration, '-r', $Runtime, '--self-contained', 'true', '-p:PublishSingleFile=false', '-o', (Join-Path $out 'gateway'))

# 3) 前端 dist + serve.mjs
$feOut = Join-Path $out 'frontend'
[void][System.IO.Directory]::CreateDirectory($feOut)
Log '复制前端 dist 与 serve.mjs…'
Copy-Item -LiteralPath (Join-Path $fe 'dist') -Destination $feOut -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'deploy\frontend\serve.mjs') -Destination (Join-Path $feOut 'serve.mjs') -Force

# 4) 便携 Node
$nodeSrc = if ($NodeExe) { $NodeExe } else { (Get-Command node -ErrorAction Stop).Source }
$nodeOut = Join-Path $out 'node'
[void][System.IO.Directory]::CreateDirectory($nodeOut)
Copy-Item -LiteralPath $nodeSrc -Destination (Join-Path $nodeOut 'node.exe') -Force
Log "内置 Node：$nodeSrc -> node\node.exe"

# 5) deploy 脚本与数据（排除 .runtime 数据）
$deployOut = Join-Path $out 'deploy'
[void][System.IO.Directory]::CreateDirectory($deployOut)
Log '复制 deploy（mysql/influxdb 脚本，排除 .runtime）…'
Copy-Item -LiteralPath (Join-Path $root 'deploy\mysql') -Destination $deployOut -Recurse -Force
Remove-Item -LiteralPath (Join-Path $deployOut 'mysql\.runtime') -Recurse -Force -ErrorAction SilentlyContinue
foreach ($extra in @('influxdb', 'gateway-data')) {
    $p = Join-Path $root "deploy\$extra"
    if (Test-Path -LiteralPath $p) { Copy-Item -LiteralPath $p -Destination $deployOut -Recurse -Force }
}
Copy-Item -LiteralPath (Join-Path $root 'deploy\probe-env.ps1') -Destination $out -Force
Copy-Item -LiteralPath (Join-Path $root 'deploy\standalone-deploy.ps1') -Destination $out -Force
Get-ChildItem -LiteralPath (Join-Path $root 'deploy') -Filter '*.ps1' -File -ErrorAction SilentlyContinue |
    ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $out -Force }

# 6) manifest
$manifest = [ordered]@{
    builtAt       = (Get-Date -Format 's')
    configuration = $Configuration
    runtime       = $Runtime
    components    = @('host', 'gateway', 'frontend')
    ports         = [ordered]@{ host = 5173; gateway = 5087; frontend = 4171; mysql = 3307; influx = 8181 }
    notes         = '独立部署（不依赖 IIS）：host/gateway 为自包含；前端由包内 node\node.exe 运行 frontend\serve.mjs 托管并反代到 gateway/host。'
}
[System.IO.File]::WriteAllText((Join-Path $out 'manifest.json'), ($manifest | ConvertTo-Json -Depth 6), $utf8)

Log "打包完成：$out"
