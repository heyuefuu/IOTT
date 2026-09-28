param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtime = Join-Path $PSScriptRoot '.runtime'
& (Join-Path $PSScriptRoot 'Start-MySql.ps1')
& (Join-Path $PSScriptRoot 'Import-InitialData.ps1')
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source
$backends = @(
    @{ Name='Host'; Directory='Host/src/IndustrialIoT.Host'; Assembly='IndustrialIoT.Host'; Port=5173 },
    @{ Name='Gateway'; Directory='MachineConnectionApi20260423/MachineConnectionApi/MachineConnectionApi'; Assembly='MachineConnectionApi'; Port=5087 }
)
$processIds = @{}
foreach ($backend in $backends) {
    $directory = Join-Path $workspaceRoot $backend.Directory
    $assemblyPath = Join-Path $directory ('bin/Debug/net8.0/' + $backend.Assembly + '.dll')
    $listener = Get-NetTCPConnection -State Listen -LocalPort $backend.Port -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $process = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
        if ($process.CommandLine -notlike "*$($backend.Assembly)*") { throw "Port $($backend.Port) is occupied by another application." }
        $processIds[$backend.Name] = $process.ProcessId
        Write-Output "$($backend.Name) already listening on $($backend.Port); restart explicitly to load rebuilt code."
        continue
    }
    if (-not $NoBuild) {
        & $dotnet build (Join-Path $directory ($backend.Assembly + '.csproj')) --verbosity quiet
        if ($LASTEXITCODE -ne 0) { throw "$($backend.Name) build failed." }
    }
    if (-not (Test-Path -LiteralPath $assemblyPath)) { throw 'Build the backends before using -NoBuild.' }
    if ($backend.Name -eq 'Gateway') {
        $appData = Join-Path (Split-Path $assemblyPath -Parent) 'App_Data'
        & (Join-Path $PSScriptRoot '../gateway-data/Restore-GatewayData.ps1') -TargetAppData $appData
    }
    $arguments = '"' + $assemblyPath + '" --environment Development --urls http://localhost:' + $backend.Port
    $process = Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $directory -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runtime ($backend.Name.ToLowerInvariant() + '.stdout.log')) `
        -RedirectStandardError (Join-Path $runtime ($backend.Name.ToLowerInvariant() + '.stderr.log')) -PassThru
    $processIds[$backend.Name] = $process.Id
    $ready = $false
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        if ($process.HasExited) { throw "$($backend.Name) exited; inspect .runtime logs." }
        try {
            Invoke-RestMethod "http://localhost:$($backend.Port)/swagger/v1/swagger.json" -TimeoutSec 2 | Out-Null
            $ready = $true
        } catch { Start-Sleep -Milliseconds 500 }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw "$($backend.Name) did not become ready; inspect .runtime logs." }
    Write-Output "$($backend.Name) ready at http://localhost:$($backend.Port)"
}
[IO.File]::WriteAllText((Join-Path $runtime 'backend-pids.json'), ($processIds | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
