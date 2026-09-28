param(
    [Parameter(Mandatory = $true)][string]$TargetAppData,
    [string]$SnapshotPath = (Join-Path $PSScriptRoot 'SeedData')
)

$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($TargetAppData).TrimEnd('\', '/')
$snapshot = [IO.Path]::GetFullPath($SnapshotPath).TrimEnd('\', '/')
$parent = Split-Path -Parent $target
if (-not $parent -or $target -eq [IO.Path]::GetPathRoot($target).TrimEnd('\', '/')) {
    throw 'TargetAppData must be a dedicated application data directory.'
}
if ($target.Equals($snapshot, [StringComparison]::OrdinalIgnoreCase) -or
    $snapshot.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The restore target must not contain the source snapshot.'
}
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$lockPath = Join-Path $parent ((Split-Path -Leaf $target) + '.seed.lock')
$restoreLock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
$stage = $null
try {
    if (Test-Path -LiteralPath $target) {
        if (-not (Test-Path -LiteralPath $target -PathType Container)) {
            throw 'TargetAppData exists as a file.'
        }
        if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'A linked application data directory cannot be initialized.'
        }
        if (@(Get-ChildItem -LiteralPath $target -Force).Count -gt 0) {
            Write-Output 'Gateway data skipped: existing App_Data is preserved.'
            return
        }
    }
    $manifestPath = Join-Path $snapshot 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.formatVersion -ne 1) { throw 'Unsupported gateway snapshot format.' }
    $entries = @($manifest.files | Where-Object { $_.path.StartsWith('App_Data/', [StringComparison]::Ordinal) })
    if ($entries.Count -eq 0) { throw 'The snapshot contains no gateway application data.' }
    $sourceRoot = Join-Path $snapshot 'App_Data'
    $sourcePrefix = [IO.Path]::GetFullPath($sourceRoot) + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $entries) {
        $source = [IO.Path]::GetFullPath((Join-Path $snapshot $entry.path))
        if (-not $source.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Snapshot file path escapes App_Data.'
        }
        $file = Get-Item -LiteralPath $source -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Snapshot entry is not a regular file: $($entry.path)"
        }
        if ($file.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Snapshot integrity check failed: $($entry.path)"
        }
    }
    $stagePrefix = Join-Path $parent '.gateway-seed-'
    $stage = [IO.Path]::GetFullPath($stagePrefix + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    foreach ($entry in $entries) {
        $relative = $entry.path.Substring('App_Data/'.Length)
        $destination = Join-Path $stage $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        [IO.File]::Copy((Join-Path $snapshot $entry.path), $destination, $false)
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Copied file integrity check failed: $relative"
        }
    }
    if (Test-Path -LiteralPath $target) {
        # Nonrecursive deletion fails if another writer has added any files.
        [IO.Directory]::Delete($target, $false)
    }
    [IO.Directory]::Move($stage, $target)
    $stage = $null
    Write-Output "Gateway data restored: $($entries.Count) files into $target"
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        $resolvedStage = [IO.Path]::GetFullPath($stage)
        if (-not $resolvedStage.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside the restore staging directory.'
        }
        [IO.Directory]::Delete($resolvedStage, $true)
    }
    $restoreLock.Dispose()
}
