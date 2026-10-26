#Requires -Version 5.1
<#
  scan-remote.ps1 - run the machine scan ON THE TARGET MACHINE over SSH and pull the
  reports back. Use this when the machine-tool LAN is only reachable from the target.

  Prereqs: OpenSSH key auth to the target works (probe-target.ps1 verifies this).

  Examples:
    .\scan-remote.ps1
    .\scan-remote.ps1 -Cidrs 192.168.0.0/24,192.168.1.0/24
    .\scan-remote.ps1 -Target 192.168.1.200 -SshUser ktb -TimeoutMs 600
#>
[CmdletBinding()]
param(
    [string]$Target = '192.168.1.200',
    [string]$SshUser = 'ktb',
    [string]$Key = (Join-Path $env:USERPROFILE '.ssh\id_ed25519_obugs'),
    [string]$RemoteDir = 'C:/Users/ktb/iott-probe',
    [string[]]$Cidrs,
    [int]$TimeoutMs = 450,
    [switch]$IncludeWlan
)

$ErrorActionPreference = 'Continue'
function Log($m) { Write-Host "[remote] $m" -ForegroundColor Cyan }
function Warn($m) { Write-Host "[remote] $m" -ForegroundColor Yellow }
function Ok($m) { Write-Host "[remote] $m" -ForegroundColor Green }

$outDir = Join-Path $PSScriptRoot 'out'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not (Test-Path $Key)) { Warn "key not found: $Key"; exit 2 }
$sshArgs = @('-i', $Key, '-o', 'BatchMode=yes', '-o', 'ConnectTimeout=8', '-o', 'StrictHostKeyChecking=accept-new', "$SshUser@$Target")

Log "checking ssh to ${SshUser}@${Target} ..."
$h = & ssh @sshArgs 'hostname' 2>&1
if ($LASTEXITCODE -ne 0) { Warn "ssh failed: $($h -join ' ')"; exit 2 }
Ok "target hostname: $($h -join ' ')"

$remoteDirWin = $RemoteDir -replace '/', '\'
Log "preparing $remoteDirWin on target"
& ssh @sshArgs "if not exist `"$remoteDirWin`" mkdir `"$remoteDirWin`"" | Out-Null

$items = @('ProbeKit.ps1', 'scan-machines.ps1')
foreach ($it in $items) {
    $local = Join-Path $PSScriptRoot $it
    if (-not (Test-Path $local)) { Warn "missing local file: $local"; exit 2 }
    Log "upload $it"
    & scp -i $Key -o StrictHostKeyChecking=accept-new $local "${SshUser}@${Target}:$RemoteDir/" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Warn "scp failed for $it"; exit 2 }
}

$run = "powershell -NoProfile -ExecutionPolicy Bypass -File $RemoteDir/scan-machines.ps1 -TimeoutMs $TimeoutMs"
if ($Cidrs -and $Cidrs.Count -gt 0) { $run += " -Cidrs $($Cidrs -join ',')" }
if ($IncludeWlan) { $run += ' -IncludeWlan' }
Log 'running scan on target ...'
& ssh @sshArgs $run 2>&1 | ForEach-Object { Write-Host $_ }
$scanExit = $LASTEXITCODE
Log "remote scan exit=$scanExit"

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$localOut = Join-Path $outDir "remote-$stamp"
New-Item -ItemType Directory -Force -Path $localOut | Out-Null
Log "pulling reports -> $localOut"
& scp -i $Key -o StrictHostKeyChecking=accept-new -r "${SshUser}@${Target}:$RemoteDir/out" $localOut 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { Warn 'could not pull out/ (reports remain on target)'; exit $scanExit }

$md = Get-ChildItem $localOut -Recurse -Filter 'latest.md' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($md) {
    Ok "report: $($md.FullName)"
    Write-Host ''
    Get-Content $md.FullName | ForEach-Object { Write-Host $_ }
} else {
    Warn "no latest.md in $localOut"
}
exit $scanExit
