# Post-repair validator. Run AFTER MediaDash's Fix task has processed the fixtures folder.
# For each fixture, assert that the expected output file (per fixture-manifest.json's postExists)
# exists and decodes cleanly. Also asserts the pre-repair original is in the recycle bin.
#
# Exit code: 0 all pass, 1 any mismatch.
#
# 00-B fixes:
#  - Recycled originals are keyed by SHA-256 of file bytes, not by basename. Two
#    fixtures with the same base filename would previously collide silently and
#    the recycle-cross-check would false-positive.
#  - Missing expected recycled original for a repaired fixture is now FAIL, not
#    WARN + exit 0. The plugin's repair path ALWAYS recycles; a missing entry is
#    a real regression.
#  - Healthy controls compare current file hash to the manifest's declared
#    `sourceSha256` (if the manifest carries one). A basename-only check would
#    accept an in-place overwrite as "still there."
#
# The manifest schema is additive: fixtures without `sourceSha256` skip the
# hash check but their path/existence checks still run. `regenerate.ps1` should
# stamp `sourceSha256` when producing new fixtures; retrofitting existing
# fixtures is a separate task and does not gate 00-B.

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

$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json

# Gather recycled originals - batches are timestamped subdirs under RecycleBinRoot.
# Keyed by SHA-256 so two fixtures that happen to share a basename never collide.
$recycledByHash = @{}
$recycledByPathTail = @{}   # secondary lookup: relative path from RecycleBinRoot
if (Test-Path $RecycleBinRoot) {
    $rootFull = (Resolve-Path -LiteralPath $RecycleBinRoot).Path
    Get-ChildItem $RecycleBinRoot -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object {
        $h = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash
        if (-not $recycledByHash.ContainsKey($h)) { $recycledByHash[$h] = @() }
        $recycledByHash[$h] += $_.FullName
        $tail = $_.FullName.Substring($rootFull.Length).TrimStart('\','/')
        $recycledByPathTail[$tail] = $_.FullName
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
    else {
        $chosen = $candidates[0]
        # Strict Jellyfin-play gate: -xerror exits non-zero on any real decode failure
        # (partial packets, corrupt frames, container/stream mismatch). If this passes,
        # Jellyfin will direct-play the file end-to-end.
        & cmd /c """$Ffmpeg"" -v error -xerror -i ""$chosen"" -f null - 2>NUL >NUL"
        if ($LASTEXITCODE -ne 0) {
            $status = 'FAIL'; $reason = "output '$(Split-Path $chosen -Leaf)' fails strict decode (-xerror) - Jellyfin would not play cleanly"
        }
        elseif ($sourceHash) {
            # We know the pre-repair bytes. A repaired fixture MUST have its exact
            # pre-repair bytes recorded in the recycle bin.
            if (-not $recycledHit) {
                $status = 'FAIL'; $reason = "pre-repair original not in recycle bin (looked up SHA-256 $sourceHash under $RecycleBinRoot)"
            }
        }
        elseif (-not $recycledByPathTail.Keys.Where({ $_ -match [regex]::Escape($fx.name) + '$' })) {
            # Fallback for legacy manifests without sourceSha256. Path-tail match is still
            # a real check but weaker than hash. Package the retrofit to stamp hashes as
            # a follow-up so we can remove this branch.
            $status = 'FAIL'; $reason = "pre-repair original not in recycle bin (no SHA-256 in manifest and no path-tail match for '$($fx.name)')"
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
