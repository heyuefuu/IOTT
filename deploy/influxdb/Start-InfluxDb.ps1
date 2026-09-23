param(
    [string]$GatewaySettings = '',
    [ValidateRange(1, 65535)][int]$Port = 8181,
    [string]$BindAddress = '127.0.0.1'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($GatewaySettings)) {
    $GatewaySettings = Join-Path $workspaceRoot 'MachineConnectionApi20260423\MachineConnectionApi\MachineConnectionApi\appsettings.json'
}
$settings = Get-Content -LiteralPath $GatewaySettings -Raw -Encoding UTF8 | ConvertFrom-Json
$database = [string]$settings.InfluxDB.Bucket
$measurement = [string]$settings.InfluxDB.Measurement
$gatewayToken = [string]$settings.InfluxDB.Token
if ($database -notmatch '^[A-Za-z0-9][A-Za-z0-9_-]*$' -or $measurement -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
    throw 'InfluxDB Bucket or Measurement is not a valid identifier.'
}
if ([string]::IsNullOrWhiteSpace($gatewayToken)) {
    throw 'Set InfluxDB:Token in GatewaySettings before initializing the local database.'
}
Get-Command docker.exe -ErrorAction Stop | Out-Null
$runtimeDirectory = Join-Path $PSScriptRoot '.runtime'
New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
$tokenPath = Join-Path $runtimeDirectory 'admin-token.json'
if (-not (Test-Path -LiteralPath $tokenPath)) {
    $bootstrap = @{ token = $gatewayToken; name = 'iott-local' } | ConvertTo-Json
    [IO.File]::WriteAllText($tokenPath, $bootstrap, [Text.UTF8Encoding]::new($false))
}
$localToken = [string](Get-Content -LiteralPath $tokenPath -Raw -Encoding UTF8 | ConvertFrom-Json).token
if ([string]::IsNullOrWhiteSpace($localToken)) { throw 'The existing local token file is empty.' }
$composeArguments = @('compose', '-f', (Join-Path $PSScriptRoot 'compose.yaml'))

function Invoke-LocalInfluxCommand {
    param([string[]]$CommandArguments)
    $previousErrorAction = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $commandOutput = & docker.exe @composeArguments exec -T --env INFLUXDB3_AUTH_TOKEN influxdb influxdb3 @CommandArguments 2>&1
        $commandExitCode = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousErrorAction
    }
    $outputText = ($commandOutput | Out-String).Replace($localToken, '[redacted]')
    if ($commandExitCode -ne 0 -and $outputText -notmatch '(?i)already exists') {
        throw $outputText.Trim()
    }
}

$environmentNames = @('IOTT_INFLUX_BIND_ADDRESS', 'IOTT_INFLUX_PORT', 'INFLUXDB3_AUTH_TOKEN')
$previousEnvironment = @{}
foreach ($environmentName in $environmentNames) {
    $previousEnvironment[$environmentName] = [Environment]::GetEnvironmentVariable($environmentName, 'Process')
}
try {
    [Environment]::SetEnvironmentVariable('IOTT_INFLUX_BIND_ADDRESS', $BindAddress, 'Process')
    [Environment]::SetEnvironmentVariable('IOTT_INFLUX_PORT', [string]$Port, 'Process')
    [Environment]::SetEnvironmentVariable('INFLUXDB3_AUTH_TOKEN', $localToken, 'Process')
    & docker.exe @composeArguments up --detach
    if ($LASTEXITCODE -ne 0) { throw 'Starting InfluxDB failed. Check that Docker Desktop is running.' }

    $probeHost = if ($BindAddress -eq '0.0.0.0') { '127.0.0.1' } else { $BindAddress }
    $localUrl = 'http://{0}:{1}' -f $probeHost, $Port
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    $ready = $false
    do {
        try {
            Invoke-RestMethod -Uri ($localUrl + '/health') -TimeoutSec 3 | Out-Null
            $ready = $true
        } catch {
            Start-Sleep -Seconds 1
        }
    } while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
    if (-not $ready) { throw ('InfluxDB did not become ready at ' + $localUrl) }

    Invoke-LocalInfluxCommand -CommandArguments @('create', 'database', $database)
    $tags = 'device_id,path,point_name,data_type,status'
    $fields = 'quality:utf8,error:utf8,value_s:utf8,value_i:int64,value_f:float64,value_b:bool,value_present:bool'
    Invoke-LocalInfluxCommand -CommandArguments @('create', 'table', '--database', $database, '--tags', $tags, '--fields', $fields, $measurement)
    $sql = 'SELECT COUNT(*) AS total FROM "' + $measurement + '"'
    Invoke-LocalInfluxCommand -CommandArguments @('query', '--database', $database, $sql)
    Write-Output ('InfluxDB ready: {0}; database={1}; table={2}; volume=iott-influxdb3-data' -f $localUrl, $database, $measurement)
    if ($localToken -cne $gatewayToken) {
        Write-Warning 'The existing local token differs from GatewaySettings. Use the existing local token in History Storage Settings.'
    }
} finally {
    foreach ($environmentName in $environmentNames) {
        [Environment]::SetEnvironmentVariable($environmentName, $previousEnvironment[$environmentName], 'Process')
    }
}
