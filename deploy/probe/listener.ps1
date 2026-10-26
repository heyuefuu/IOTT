#Requires -Version 5.1
<#
  listener.ps1 - run on THIS PC to receive reverse-agent.ps1 from the target machine.
  Interactive by default; -Commands runs a batch and exits.

  Examples:
    .\listener.ps1
    .\listener.ps1 -Commands info,subnets,listening,arp,firewall
    .\listener.ps1 -Port 4444 -Log .\out\agent.log
#>
[CmdletBinding()]
param(
    [int]$Port = 4444,
    [string]$Log,
    [string[]]$Commands,
    [int]$AcceptTimeoutSec = 300,
    [int]$ReadTimeoutSec = 600
)

$ErrorActionPreference = 'Continue'
if (-not $Log) {
    $dir = Join-Path $PSScriptRoot 'out'
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $Log = Join-Path $dir ("agent-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".log")
}
function LogLine($t) { $t | Tee-Object -FilePath $Log -Append | Out-Host }

$listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Any, $Port)
$listener.Start()
Write-Host "[listener] waiting for reverse agent on 0.0.0.0:$Port (log: $Log) ..." -ForegroundColor Cyan

$client = $null
$sw = [Diagnostics.Stopwatch]::StartNew()
while (-not $client -and $sw.Elapsed.TotalSeconds -lt $AcceptTimeoutSec) {
    if ($listener.Pending()) { $client = $listener.AcceptTcpClient() } else { Start-Sleep -Milliseconds 300 }
}
if (-not $client) {
    $listener.Stop()
    Write-Host "[listener] no agent connected within ${AcceptTimeoutSec}s" -ForegroundColor Yellow
    exit 2
}
$remote = $client.Client.RemoteEndPoint.ToString()
Write-Host "[listener] agent connected from $remote" -ForegroundColor Green

$stream = $client.GetStream()
$stream.ReadTimeout = $ReadTimeoutSec * 1000
$reader = New-Object IO.StreamReader($stream, [Text.Encoding]::UTF8)
$writer = New-Object IO.StreamWriter($stream, [Text.Encoding]::UTF8)
$writer.AutoFlush = $true

$hello = $reader.ReadLine()
Write-Host "[listener] $hello" -ForegroundColor Gray
LogLine "[$remote] $hello"

function Invoke-AgentCommand([string]$cmd) {
    $writer.WriteLine($cmd)
    $line = $reader.ReadLine()
    if ($null -eq $line) { return $null }
    try { return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line)) }
    catch { return $line }
}

if ($Commands -and $Commands.Count -gt 0) {
    foreach ($c in $Commands) {
        Write-Host "`n===== $c =====" -ForegroundColor Cyan
        LogLine "`n===== $c ====="
        $out = Invoke-AgentCommand $c
        if ($null -eq $out) { Write-Host '(connection closed)' -ForegroundColor Yellow; break }
        LogLine $out
    }
    [void](Invoke-AgentCommand 'quit')
    $client.Close(); $listener.Stop()
    Write-Host "`n[listener] done. log: $Log" -ForegroundColor Green
    exit 0
}

Write-Host '[listener] type a command (info/subnets/scan/firewall/listening/arp/quit; empty = reprint info)' -ForegroundColor Gray
while ($true) {
    $cmd = Read-Host 'agent'
    if ($null -eq $cmd) { break }
    $cmd = $cmd.Trim()
    if (-not $cmd) { $cmd = 'info' }
    if ($cmd -eq 'quit' -or $cmd -eq 'exit') {
        [void](Invoke-AgentCommand 'quit')
        break
    }
    $out = Invoke-AgentCommand $cmd
    if ($null -eq $out) { Write-Host '(connection closed)' -ForegroundColor Yellow; break }
    LogLine "`n===== $cmd ====="
    LogLine $out
}

$client.Close(); $listener.Stop()
Write-Host "[listener] closed. log: $Log" -ForegroundColor Green
