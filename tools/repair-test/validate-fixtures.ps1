# Pre-repair validator. For each fixture, assert that:
#   preBroken = true  → ffmpeg decode returns non-zero (file is genuinely broken)
#   preBroken = false → ffmpeg decode returns zero (file is genuinely healthy)
# If any expectation flips, the fixture regeneration is wrong and repair-ladder tests
# would run against the wrong baseline.
#
# Exit code: 0 all pass, 1 any mismatch.

param([string]$Ffmpeg = '',
      [string]$ManifestPath = (Join-Path $PSScriptRoot 'fixture-manifest.json'),
      [string]$FixturesDir = (Join-Path $PSScriptRoot 'fixtures'))

$ErrorActionPreference = 'Stop'

if (-not $Ffmpeg) {
    $jf = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe"
    if (Test-Path $jf) { $Ffmpeg = $jf } else { $Ffmpeg = 'ffmpeg' }
}

$Ffmpeg = (Get-Command $Ffmpeg -ErrorAction Stop).Source
$ffprobe = Join-Path (Split-Path $Ffmpeg -Parent) ('ffprobe' + [IO.Path]::GetExtension($Ffmpeg))
$null = Get-Command $ffprobe -ErrorAction Stop
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$fixtures = $FixturesDir
if (@($manifest.fixtures).Count -eq 0) { throw 'Manifest contains no fixtures.' }

# Mirror PlayabilityScanner's DecodeCheckAsync: broken if exit non-zero OR decoded time
# falls below 90% of expected. -err_detect explode alone misses tail truncation because
# ffmpeg exits 0 at EOF; the plugin's scanner catches those via the shortfall check.
function Test-Broken($path) {
    # Two-gate check: strict exit-code AND shortfall heuristic. Broken if either fires.
    & $Ffmpeg -nostdin -v error -xerror -err_detect explode -i $path -map '0:v?' -map '0:a?' -f null - 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { return $true }

    $durStr = (& $ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 $path).Trim()
    if (-not $durStr) { return $true }
    $dur = [double]$durStr
    if ($dur -le 0) { return $true }
    $expected = [Math]::Min(30.0, $dur)

    $decodeOut = & $Ffmpeg -nostdin -threads 4 -xerror -v error -stats -i $path -map '0:v?' -map '0:a?' -t 30 -f null - 2>&1
    if ($LASTEXITCODE -ne 0) { return $true }
    $timeMatch = ($decodeOut | Select-String -Pattern 'time=(\d\d):(\d\d):([\d.]+)' | Select-Object -Last 1)
    if (-not $timeMatch) { return $true }
    $g = $timeMatch.Matches[0].Groups
    $decoded = [int]$g[1].Value * 3600 + [int]$g[2].Value * 60 + [double]$g[3].Value
    return ($decoded -lt $expected * 0.9)
}

$results = @()
foreach ($fx in $manifest.fixtures) {
    $path = Join-Path $fixtures $fx.name
    $expected = if ($fx.preBroken) { 'broken' } else { 'healthy' }
    if (-not (Test-Path -LiteralPath $path)) {
        $results += [pscustomobject]@{ name = $fx.name; expected = $expected; actual = 'MISSING'; pass = $false }
        continue
    }
    $actual = if (Test-Broken $path) { 'broken' } else { 'healthy' }
    $results += [pscustomobject]@{ name = $fx.name; expected = $expected; actual = $actual; pass = ($expected -eq $actual) }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { -not $_.pass })
if ($failed.Count -gt 0) {
    Write-Host "`n$($failed.Count) fixture(s) do not match manifest expectations. Re-run regenerate.ps1 or update the manifest."
    exit 1
}
Write-Host "`nAll $($results.Count) fixtures match their pre-repair expectations."
exit 0
