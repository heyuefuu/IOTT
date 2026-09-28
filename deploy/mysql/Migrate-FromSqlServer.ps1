param(
    [ValidateSet('Inspect', 'Apply', 'Verify')][string]$Mode = 'Inspect'
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtime = Join-Path $PSScriptRoot '.runtime'
$connections = Get-Content -LiteralPath (Join-Path $runtime 'app-connections.json') -Raw | ConvertFrom-Json
$sourceHost = $env:SQLSERVER_HOST_CONNECTION
$sourceGateway = $env:SQLSERVER_GATEWAY_CONNECTION
if (-not $sourceHost) {
    $sourceHost = 'Server=(localdb)\MSSQLLocalDB;Database=IndustrialIoT;Integrated Security=true;TrustServerCertificate=true;'
}
if (-not $sourceGateway) {
    $sourceGateway = 'Server=(localdb)\MSSQLLocalDB;Database=MachineCollection;Integrated Security=true;TrustServerCertificate=true;'
}
if ($Mode -eq 'Apply') {
    $running = Get-CimInstance Win32_Process | Where-Object {
        ($_.Name -in @('IndustrialIoT.Host.exe', 'MachineConnectionApi.exe') -and
            $_.ExecutablePath -like "$workspaceRoot\*") -or
        ($_.Name -eq 'dotnet.exe' -and $_.CommandLine -match '(IndustrialIoT\.Host|MachineConnectionApi)\.(dll|csproj)' -and
            ($_.CommandLine -like "*$workspaceRoot*" -or $_.CommandLine -match '(Host[\\/]src|MachineConnectionApi20260423)'))
    }
    if ($running) { throw 'Stop the project backends before Apply so source data cannot change during copying.' }
    $backupDirectory = Join-Path $runtime ('sqlserver-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    foreach ($source in @($sourceHost, $sourceGateway)) {
        $connection = New-Object System.Data.SqlClient.SqlConnection $source
        try {
            $connection.Open()
            $databaseName = $connection.Database
            if ($databaseName -notmatch '^[A-Za-z0-9_-]+$') { throw 'Unsupported source database name for automatic backup.' }
            $backupPath = Join-Path $backupDirectory ($databaseName + '.bak')
            $command = $connection.CreateCommand()
            $command.CommandTimeout = 120
            $command.CommandText = "BACKUP DATABASE [$databaseName] TO DISK=@path WITH COPY_ONLY,CHECKSUM; RESTORE VERIFYONLY FROM DISK=@path WITH CHECKSUM;"
            $command.Parameters.AddWithValue('@path', $backupPath) | Out-Null
            $command.ExecuteNonQuery() | Out-Null
            Write-Output "Verified SQL Server backup: $backupPath"
        } finally {
            $connection.Dispose()
        }
    }
}
$environmentValues = @{
    SQLSERVER_HOST_CONNECTION = $sourceHost
    SQLSERVER_GATEWAY_CONNECTION = $sourceGateway
    MYSQL_HOST_CONNECTION = $connections.IndustrialIoT
    MYSQL_GATEWAY_CONNECTION = $connections.MachineCollection
}
$previousEnvironment = @{}
try {
    foreach ($key in $environmentValues.Keys) {
        $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
        [Environment]::SetEnvironmentVariable($key, $environmentValues[$key], 'Process')
    }
    $project = Join-Path $PSScriptRoot 'Migrate/Migrate.csproj'
    $artifacts = Join-Path $workspaceRoot 'tmp/mysql-migrate-build'
    & dotnet build $project --artifacts-path $artifacts --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Migration tool build failed.' }
    $assembly = Join-Path $artifacts 'bin/Migrate/debug/Migrate.dll'
    $report = Join-Path $runtime ('migration-' + $Mode.ToLowerInvariant() + '.json')
    & dotnet $assembly ('--' + $Mode.ToLowerInvariant()) $report
    if ($LASTEXITCODE -ne 0) { throw "Migration $Mode failed. Source databases were not modified." }
} finally {
    foreach ($key in $previousEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], 'Process')
    }
}
