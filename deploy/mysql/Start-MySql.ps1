param(
    [string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.4\bin',
    [ValidateRange(1024, 65535)][int]$Port = 3307
)

$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runtime = Join-Path $PSScriptRoot '.runtime'
$dataDirectory = Join-Path $runtime 'data'
$server = Join-Path $MySqlBin 'mysqld.exe'
$client = Join-Path $MySqlBin 'mysql.exe'
$statePath = Join-Path $runtime 'instance.json'
$clientConfig = Join-Path $runtime 'client.cnf'
$configPath = Join-Path $runtime 'my.ini'
$utf8 = [Text.UTF8Encoding]::new($false)
if (-not (Test-Path -LiteralPath $server) -or -not (Test-Path -LiteralPath $client)) {
    throw 'MySQL 8.4 binaries are required. Supply -MySqlBin with the installed bin directory.'
}
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
$startupLock = [IO.File]::Open((Join-Path $runtime 'startup.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
if (Test-Path -LiteralPath $statePath) {
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.Port -ne $Port) { throw 'The existing instance uses another port; supply its original -Port.' }
} else {
    if (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue) {
        throw "Port $Port is already occupied. No existing database was changed."
    }
    $state = [pscustomobject]@{ Port = $Port; RootPassword = ''; AppPassword = '' }
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    foreach ($property in @('RootPassword', 'AppPassword')) {
        $bytes = New-Object byte[] 32
        $random.GetBytes($bytes)
        $state.$property = ([BitConverter]::ToString($bytes)).Replace('-', '').ToLowerInvariant()
    }
    $random.Dispose()
    [IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json), $utf8)
}
$clientText = "[client]`nhost=127.0.0.1`nport=$Port`nuser=root`npassword=$($state.RootPassword)`nprotocol=tcp`n"
[IO.File]::WriteAllText($clientConfig, $clientText, $utf8)
$dataPath = $dataDirectory.Replace('\', '/')
$runtimePath = $runtime.Replace('\', '/')
$serverConfig = @"
[mysqld]
basedir="$((Split-Path $MySqlBin -Parent).Replace('\', '/'))"
datadir="$dataPath"
port=$Port
bind-address=127.0.0.1
mysqlx=0
skip-name-resolve=ON
character-set-server=utf8mb4
collation-server=utf8mb4_0900_ai_ci
default-time-zone=+00:00
log-error="$runtimePath/mysql-error.log"
pid-file="$runtimePath/mysql.pid"
"@
[IO.File]::WriteAllText($configPath, $serverConfig, $utf8)
$newDatabase = -not (Test-Path -LiteralPath (Join-Path $dataDirectory 'mysql'))
if ($newDatabase) {
    & $server "--defaults-file=$configPath" --initialize-insecure
    if ($LASTEXITCODE -ne 0) { throw 'MySQL initialization failed; inspect .runtime/mysql-error.log.' }
}
$processDeadline = [DateTime]::UtcNow.AddSeconds(30)
do {
    $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue
    $existingServer = Get-CimInstance Win32_Process -Filter "Name='mysqld.exe'" | Where-Object {
        $_.CommandLine -like ('*' + $configPath + '*')
    }
    if ($listener -or -not $existingServer) { break }
    Start-Sleep -Milliseconds 500
} while ([DateTime]::UtcNow -lt $processDeadline)
if (-not $listener -and $existingServer) { throw 'The project MySQL process is still starting or stopping. Retry after it finishes.' }
if (-not $listener) {
    $serverArguments = @('--defaults-file="' + $configPath + '"')
    if (-not $state.PSObject.Properties['Provisioned']) {
        $initPath = Join-Path $runtime 'initialize.sql'
        $initSql = @"
ALTER USER 'root'@'localhost' IDENTIFIED BY '$($state.RootPassword)';
CREATE USER IF NOT EXISTS 'root'@'127.0.0.1' IDENTIFIED BY '$($state.RootPassword)';
GRANT ALL PRIVILEGES ON *.* TO 'root'@'127.0.0.1' WITH GRANT OPTION;
"@
        [IO.File]::WriteAllText($initPath, $initSql, $utf8)
        $serverArguments += '--init-file="' + $initPath + '"'
    }
    Start-Process -FilePath $server -ArgumentList $serverArguments -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $runtime 'server.stdout.log') `
        -RedirectStandardError (Join-Path $runtime 'server.stderr.log') | Out-Null
}
$deadline = [DateTime]::UtcNow.AddSeconds(45)
$ready = $false
do {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $actualDirectory = & $client "--defaults-extra-file=$clientConfig" --connect-timeout=2 --batch --skip-column-names '--execute=SELECT @@datadir' 2>$null
    $ready = $LASTEXITCODE -eq 0
    $ErrorActionPreference = $previousPreference
    if (-not $ready) { Start-Sleep -Milliseconds 500 }
} while (-not $ready -and [DateTime]::UtcNow -lt $deadline)
if (-not $ready) { throw 'MySQL did not become ready; inspect .runtime/mysql-error.log.' }
if ([IO.Path]::GetFullPath($actualDirectory.Trim()).TrimEnd('\', '/') -ine $dataDirectory.TrimEnd('\', '/')) {
    throw 'Port is owned by a different MySQL data directory. No accounts or databases were changed.'
}
$bootstrap = @"
CREATE DATABASE IF NOT EXISTS IndustrialIoT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE DATABASE IF NOT EXISTS MachineCollection CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
CREATE USER IF NOT EXISTS 'iott'@'127.0.0.1' IDENTIFIED BY '$($state.AppPassword)';
GRANT ALL PRIVILEGES ON IndustrialIoT.* TO 'iott'@'127.0.0.1';
GRANT ALL PRIVILEGES ON MachineCollection.* TO 'iott'@'127.0.0.1';
"@
$bootstrap | & $client "--defaults-extra-file=$clientConfig" --default-character-set=utf8mb4
if ($LASTEXITCODE -ne 0) { throw 'MySQL database/account provisioning failed.' }
if (Test-Path -LiteralPath (Join-Path $runtime 'initialize.sql')) {
    Remove-Item -LiteralPath (Join-Path $runtime 'initialize.sql')
}
$state | Add-Member -NotePropertyName Provisioned -NotePropertyValue $true -Force
[IO.File]::WriteAllText($statePath, ($state | ConvertTo-Json), $utf8)
$connections = @{}
foreach ($database in @('IndustrialIoT', 'MachineCollection')) {
    $connections[$database] = "Server=127.0.0.1;Port=$Port;Database=$database;User ID=iott;Password=$($state.AppPassword);DateTimeKind=Utc;"
}
[IO.File]::WriteAllText((Join-Path $runtime 'app-connections.json'), ($connections | ConvertTo-Json), $utf8)
$projects = @(
    @{ Directory = 'Host/src/IndustrialIoT.Host'; Key = 'Default'; Database = 'IndustrialIoT' },
    @{ Directory = 'MachineConnectionApi20260423/MachineConnectionApi/MachineConnectionApi'; Key = 'MachineCollection'; Database = 'MachineCollection' }
)
foreach ($project in $projects) {
    $settingsPath = Join-Path (Join-Path $workspaceRoot $project.Directory) 'appsettings.Local.json'
    $settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
    if (-not $settings.PSObject.Properties['ConnectionStrings']) { $settings | Add-Member ConnectionStrings ([pscustomobject]@{}) }
    $settings.ConnectionStrings | Add-Member -NotePropertyName $project.Key -NotePropertyValue $connections[$project.Database] -Force
    $settings | Add-Member -NotePropertyName Database -NotePropertyValue @{ ServerVersion = '8.4.0' } -Force
    [IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 30), $utf8)
}
Write-Output "MySQL ready at 127.0.0.1:$Port; databases=IndustrialIoT,MachineCollection; local settings written."
} finally {
    $startupLock.Dispose()
}
