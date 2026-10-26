$ErrorActionPreference = 'Continue'
$out = Join-Path $env:TEMP 'find405'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$lines = New-Object System.Collections.Generic.List[string]
function W($s) { $lines.Add($s) | Out-Null; Write-Host $s }

W "output dir = $out"

# 1) IIS 访问日志（Host/网关都由 IIS 承载）——405 一定有记录
$iisRoots = @('C:\inetpub\logs\LogFiles')
foreach ($root in $iisRoots) {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem $root -Recurse -Filter *.log -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 4 | ForEach-Object {
        W ""
        W "== IIS ${_} =="
        $hits = Select-String -Path $_.FullName -Pattern ' 405 ' -ErrorAction SilentlyContinue | Select-Object -Last 60
        if (-not $hits) { W "  （无 405）"; return }
        foreach ($h in $hits) {
            $t = ($h.Line -split '\s+')
            $i = [array]::IndexOf($t, '405')
            if ($i -gt 0) {
                $method = $t[3]; $uri = $t[4] + $(if ($t[5] -and $t[5] -ne '-') { '?' + $t[5] } else { '' })
                W ("  [{0}] {1} {2}" -f $h.LineNumber, $method, $uri)
            } else { W ("  [{0}] {1}" -f $h.LineNumber, $h.Line) }
        }
    }
}

# 2) 应用自身日志（Host / 网关）
foreach ($p in 'C:\iott-bundle\host\logs', 'C:\iott-bundle\gateway\logs') {
    if (-not (Test-Path $p)) { continue }
    Get-ChildItem $p -Recurse -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 2 | ForEach-Object {
        W ""
        W "== $($_.FullName) =="
        $hits = Select-String -Path $_.FullName -Pattern '405|Method Not Allowed|NotAllowed' -ErrorAction SilentlyContinue | Select-Object -Last 40
        if (-not $hits) { W "  （无 405 相关行）" } else { $hits | ForEach-Object { W ("  " + $_.Line) } }
    }
}

# 3) 网关最近的请求日志（若有 stdout 访问日志）
W ""
W "== 提示：也可在浏览器 F12 → Network 里，复现“点位数据采集”，把那条 405 的 Request URL / Method / Response 贴回来 =="

[System.IO.File]::WriteAllLines("$out\405.txt", $lines, (New-Object System.Text.UTF8Encoding($false)))
"report: $out\405.txt"
