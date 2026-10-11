# Offline post-repair checks. Use run_live.py for current-run history and Jellyfin evidence.
param(
    [string]$Ffmpeg = '',
    [string]$RecycleBinRoot = "$env:LOCALAPPDATA\jellyfin-v10\data\mediadash\recycle",
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'fixture-manifest.json'),
    [string]$FixturesDir  = (Join-Path $PSScriptRoot 'fixtures')
)

$ErrorActionPreference = 'Stop'

if (-not $Ffmpeg) {
    $jf = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe"
    if (Test-Path $jf) { $Ffmpeg = $jf } else { $Ffmpeg = 'ffmpeg' }
}

function Get-Sha256Hex {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash
}

$null = Get-Command $Ffmpeg -ErrorAction Stop
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if (@($manifest.fixtures).Count -eq 0) { throw 'Manifest contains no fixtures.' }
foreach ($fixture in $manifest.fixtures) {
    if (-not $fixture.name -or [string]$fixture.sourceSha256 -notmatch '^[a-fA-F0-9]{64}$') {
        throw 'Every fixture requires a name and pre-repair sourceSha256. Regenerate the fixtures.'
    }
}

# Gather recycled originals - batches are timestamped subdirs under RecycleBinRoot.
# Keyed by SHA-256 so two fixtures that happen to share a basename never collide.
$recycledByHash = @{}
if (Test-Path $RecycleBinRoot) {
    Get-ChildItem $RecycleBinRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        if (-not $recycledByHash.ContainsKey($h)) { $recycledByHash[$h] = @() }
        $recycledByHash[$h] += $_.FullName
    }
}

$results = @()
foreach ($fx in $manifest.fixtures) {
    # postExists is the manifest's "target rung would produce this file" hint. Reality: the
    # plugin walks the ladder and stops at the FIRST successful rung, which may land the
    # output at either the source's original path (rungs 1/2) or with the extension changed
    # to .mkv (rungs 3/4). Both are correct outcomes from a user perspective - "my file is
    # playable somewhere reasonable" - so accept either.
    $originalPath = Join-Path $FixturesDir $fx.name
    $mkvVariant   = Join-Path $FixturesDir ([IO.Path]::ChangeExtension($fx.name, '.mkv'))
    $candidates = @(@($originalPath, $mkvVariant) | Select-Object -Unique | Where-Object { Test-Path -LiteralPath $_ })

    $status = 'PASS'
    $reason = ''
    $sourceHash = if ($fx.PSObject.Properties['sourceSha256']) { [string]$fx.sourceSha256 } else { $null }
    $recycledHit = $null
    if ($sourceHash) { $recycledHit = $recycledByHash[$sourceHash] }

    if ($fx.targetRung -eq 0) {
        # Negative control - must be untouched at original path AND (if hash known) bytes intact.
        if (-not (Test-Path -LiteralPath $originalPath)) {
            $status = 'FAIL'; $reason = "healthy fixture no longer at original path (false-positive delete)"
        }
        elseif ($sourceHash -and (Get-Sha256Hex $originalPath) -ne $sourceHash) {
            $status = 'FAIL'; $reason = "healthy fixture bytes changed in place (source SHA-256 no longer matches manifest)"
        }
        elseif ($recycledHit) {
            $status = 'FAIL'; $reason = "healthy fixture was recycled (false positive; hash matches an entry in $RecycleBinRoot)"
        }
        $chosen = $originalPath
    }
    elseif ($candidates.Count -eq 0) {
        $status = 'FAIL'; $reason = "no repaired output at '$($fx.name)' or its .mkv variant - plugin fell through to delete"
        $chosen = ''
    }
    elseif ($candidates.Count -gt 1) {
        $status = 'FAIL'; $reason = 'Ambiguous output: both original and changed-extension files exist.'
        $chosen = ''
    }
    else {
        $chosen = $candidates[0]
        # Decode every audio/video stream. Client playback needs separate live verification.
        & $Ffmpeg -nostdin -v error -xerror -i $chosen -map '0:v?' -map '0:a?' -f null - 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            $status = 'FAIL'; $reason = "output '$(Split-Path $chosen -Leaf)' fails strict decode (-xerror) - decode failed"
        }
        elseif ($sourceHash) {
            # We know the pre-repair bytes. A repaired fixture MUST have its exact
            # pre-repair bytes recorded in the recycle bin.
            if (-not $recycledHit) {
                $status = 'FAIL'; $reason = "pre-repair original not in recycle bin (looked up SHA-256 $sourceHash under $RecycleBinRoot)"
            }
        }

    }

    $results += [pscustomobject]@{
        name = $fx.name
        rung = $fx.targetRung
        actualOutput = if ($chosen) { Split-Path $chosen -Leaf } else { '(none)' }
        status = $status
        reason = $reason
    }
}

$results | Format-Table -AutoSize

$failed = @($results | Where-Object { $_.status -eq 'FAIL' })

if ($failed.Count -gt 0) {
    Write-Host "`n$($failed.Count) fixture(s) failed post-repair verification."
    exit 1
}
Write-Host "`nAll $($results.Count) fixtures repaired as expected."
exit 0
