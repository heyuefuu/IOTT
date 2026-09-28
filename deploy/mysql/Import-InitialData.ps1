param([string]$MySqlBin = 'C:\Program Files\MySQL\MySQL Server 8.4\bin')
$ErrorActionPreference = 'Stop'
$runtime = Join-Path $PSScriptRoot '.runtime'
$client = Join-Path $MySqlBin 'mysql.exe'
$clientConfig = Join-Path $runtime 'client.cnf'
$snapshot = Join-Path $PSScriptRoot 'SeedData/initial-data.sql'
foreach ($requiredPath in @($client, $clientConfig, $snapshot)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Required file missing: $requiredPath. Run Start-MySql.ps1 first." }
}
$seedLock = [IO.File]::Open((Join-Path $runtime 'seed.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
$pending = Join-Path $runtime 'initial-data.pending'
$previousOutputEncoding = $OutputEncoding
try {
    if (Test-Path -LiteralPath $pending) { throw 'A previous import did not finish. Inspect the databases before retrying; existing data will not be overwritten.' }
    $query = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA IN ('IndustrialIoT','MachineCollection');"
    $tableCount = & $client "--defaults-extra-file=$clientConfig" --batch --skip-column-names "--execute=$query"
    if ($LASTEXITCODE -ne 0) { throw 'Unable to check whether the target databases are empty.' }
    if ([int]$tableCount -gt 0) {
        Write-Output 'Initial data skipped: existing database tables are preserved.'
        return
    }
    $OutputEncoding = [Text.UTF8Encoding]::new($false)
    $snapshotSql = Get-Content -LiteralPath $snapshot -Raw -Encoding UTF8
    $programUpdates = "UPDATE IndustrialIoT.NCPrograms SET LocalFilePath=NULL;`n"
    $gatewaySeed = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../gateway-data/SeedData'))
    $manifestPath = Join-Path $gatewaySeed 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($program in $manifest.programs | Where-Object available) {
            $programPath = [IO.Path]::GetFullPath((Join-Path $gatewaySeed $program.path))
            if (-not $programPath.StartsWith($gatewaySeed + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Program path is outside the data snapshot.' }
            if (-not (Test-Path -LiteralPath $programPath -PathType Leaf)) { throw "Program file missing: $($program.id)" }
            $manifestFile = @($manifest.files | Where-Object { $_.path -eq $program.path })
            if ($manifestFile.Count -ne 1 -or (Get-FileHash -LiteralPath $programPath -Algorithm SHA256).Hash -ine $manifestFile[0].sha256) { throw "Program checksum mismatch: $($program.id)" }
            $pathHex = [BitConverter]::ToString([Text.Encoding]::UTF8.GetBytes($programPath)).Replace('-', '')
            $idHex = [BitConverter]::ToString([Text.Encoding]::UTF8.GetBytes($program.id)).Replace('-', '')
            $programUpdates += "UPDATE IndustrialIoT.NCPrograms SET LocalFilePath=CONVERT(0x$pathHex USING utf8mb4) WHERE Id=CONVERT(0x$idHex USING utf8mb4);`n"
        }
    }
    $commitPattern = '(?m)^COMMIT;\r?$'
    if ([regex]::Matches($snapshotSql, $commitPattern).Count -ne 1) { throw 'Expected exactly one snapshot transaction commit.' }
    $snapshotSql = $snapshotSql -replace $commitPattern, ($programUpdates + 'COMMIT;')
    [IO.File]::WriteAllText($pending, [DateTime]::UtcNow.ToString('O'), [Text.UTF8Encoding]::new($false))
    $snapshotSql | & $client "--defaults-extra-file=$clientConfig" --default-character-set=utf8mb4 --binary-mode
    if ($LASTEXITCODE -ne 0) { throw 'Initial data import failed. Inspect the databases and initial-data.pending before retrying.' }
    Remove-Item -LiteralPath $pending
    Write-Output 'Initial data imported into the empty IndustrialIoT and MachineCollection databases.'
} finally {
    $OutputEncoding = $previousOutputEncoding
    $seedLock.Dispose()
}
