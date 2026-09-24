# validator-self-tests.ps1 - isolated tests for validate-outputs.ps1
#
# Builds synthetic fixture folders + recycle bins under a temp directory and
# invokes the real validator. Verifies the exit-decision covers:
#   A. Old recycle entry shares basename but wrong bytes -> FAIL
#   B. Healthy control modified in place -> FAIL even though filename exists
#   C. Repair fixture missing from recycle bin -> FAIL (not WARN+exit 0)
#   D. Clean successful run -> exit 0
#   E. Two fixtures with same basename but different bytes -> both correctly
#      cross-checked (no basename collision).
#
# The validator is called as a child process so its exit code is authoritative.

[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1.0

$here = Split-Path -Parent $PSCommandPath
$Validator = Join-Path $here 'validate-outputs.ps1'
if (-not (Test-Path $Validator)) { throw "validate-outputs.ps1 not found at $Validator" }

# Pick whichever PS host exists for child-process calls (see self-tests.ps1).
$Script:PsChild = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

$Script:CaseResults = @()
function Assert-Case {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    $Script:CaseResults += [pscustomobject]@{ Name = $Name; Passed = $Condition; Detail = $Detail }
    if ($Condition) {
        Write-Host "  [PASS] $Name"
    } else {
        Write-Host "  [FAIL] $Name -- $Detail"
    }
}

function New-Scenario {
    param([string]$Name)
    $root = Join-Path ([IO.Path]::GetTempPath()) ("mdvalidate-" + $Name + "-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
    $fx = Join-Path $root 'fixtures'
    $rb = Join-Path $root 'recycle'
    New-Item -ItemType Directory -Force -Path $fx | Out-Null
    New-Item -ItemType Directory -Force -Path $rb | Out-Null
    return @{ Root = $root; Fixtures = $fx; Recycle = $rb }
}

function Write-BytesTo {
    param([string]$Path, [byte[]]$Bytes)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllBytes($Path, $Bytes)
}

function Get-Sha256Hex { param([string]$Path); return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }

# Stub ffmpeg shim - the validator runs ffmpeg -xerror on repaired outputs.
# The shim always exits 0 so we exercise the decision logic, not codec parsing.
$ShimDir = Join-Path ([IO.Path]::GetTempPath()) ("mdvalidate-shim-" + [Guid]::NewGuid().ToString('N').Substring(0,8))
New-Item -ItemType Directory -Force -Path $ShimDir | Out-Null
$ShimPath = Join-Path $ShimDir 'ffmpeg-shim.cmd'
'@echo off' | Out-File -Encoding ascii $ShimPath
'exit /b 0' | Add-Content $ShimPath -Encoding ascii

function Invoke-ValidatorWithShim {
    param([string]$Fixtures, [string]$Recycle, [string]$Manifest)
    $out = & $Script:PsChild -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $Validator `
        -Ffmpeg $ShimPath `
        -ManifestPath $Manifest `
        -FixturesDir $Fixtures `
        -RecycleBinRoot $Recycle 2>&1 | Out-String
    return @{ ExitCode = $LASTEXITCODE; Output = $out }
}

Write-Host "=== validate-outputs.ps1 self-tests ==="

# D (baseline). Clean successful case: one repaired fixture, correct bytes in bin.
$s = New-Scenario 'clean-pass'
$prebytes = [byte[]](1..64)
$scratchOriginal = Join-Path $s.Root 'source.bin'
Write-BytesTo $scratchOriginal $prebytes
$srcHash = Get-Sha256Hex $scratchOriginal
Write-BytesTo (Join-Path $s.Recycle "batch1\one.mkv") $prebytes
Write-BytesTo (Join-Path $s.Fixtures "one.mkv") ([byte[]](200..250))
$m = @{ fixtures = @(@{ name = 'one.mkv'; targetRung = 1; postExists = 'one.mkv'; sourceSha256 = $srcHash }) } | ConvertTo-Json -Depth 6
$mp = Join-Path $s.Root 'manifest.json'; $m | Out-File -Encoding utf8 $mp
$r = Invoke-ValidatorWithShim -Fixtures $s.Fixtures -Recycle $s.Recycle -Manifest $mp
Assert-Case 'D. Clean repaired fixture with hash-matched bin entry -> exit 0' ($r.ExitCode -eq 0) $r.Output

# A. Recycle bin has a file with matching basename but WRONG bytes -> FAIL.
$s = New-Scenario 'basename-collision'
$prebytes = [byte[]](1..64)
$wrongBytes = [byte[]](100..163)
$scratch = Join-Path $s.Root 'source.bin'; Write-BytesTo $scratch $prebytes
$srcHash = Get-Sha256Hex $scratch
Write-BytesTo (Join-Path $s.Recycle "batch1\one.mkv") $wrongBytes
Write-BytesTo (Join-Path $s.Fixtures "one.mkv") ([byte[]](200..250))
$m = @{ fixtures = @(@{ name = 'one.mkv'; targetRung = 1; postExists = 'one.mkv'; sourceSha256 = $srcHash }) } | ConvertTo-Json -Depth 6
$mp = Join-Path $s.Root 'manifest.json'; $m | Out-File -Encoding utf8 $mp
$r = Invoke-ValidatorWithShim -Fixtures $s.Fixtures -Recycle $s.Recycle -Manifest $mp
Assert-Case 'A. Bin has basename match but wrong hash -> validator FAILs (exit 1)' ($r.ExitCode -eq 1) $r.Output

# B. Healthy control (targetRung=0) but file modified in place -> FAIL.
$s = New-Scenario 'healthy-in-place-overwrite'
$origBytes = [byte[]](50..113)
$modifiedBytes = [byte[]](14..77)
$scratch = Join-Path $s.Root 'source.bin'; Write-BytesTo $scratch $origBytes
$srcHash = Get-Sha256Hex $scratch
Write-BytesTo (Join-Path $s.Fixtures "healthy.mkv") $modifiedBytes
$m = @{ fixtures = @(@{ name = 'healthy.mkv'; targetRung = 0; postExists = 'healthy.mkv'; sourceSha256 = $srcHash }) } | ConvertTo-Json -Depth 6
$mp = Join-Path $s.Root 'manifest.json'; $m | Out-File -Encoding utf8 $mp
$r = Invoke-ValidatorWithShim -Fixtures $s.Fixtures -Recycle $s.Recycle -Manifest $mp
Assert-Case 'B. Healthy control file overwritten in place -> FAIL (exit 1)' ($r.ExitCode -eq 1) $r.Output

# C. Repaired fixture but nothing in recycle bin -> FAIL (was WARN+exit 0).
$s = New-Scenario 'missing-recycled'
$prebytes = [byte[]](1..64)
$scratch = Join-Path $s.Root 'source.bin'; Write-BytesTo $scratch $prebytes
$srcHash = Get-Sha256Hex $scratch
Write-BytesTo (Join-Path $s.Fixtures "two.mkv") ([byte[]](200..250))
$m = @{ fixtures = @(@{ name = 'two.mkv'; targetRung = 1; postExists = 'two.mkv'; sourceSha256 = $srcHash }) } | ConvertTo-Json -Depth 6
$mp = Join-Path $s.Root 'manifest.json'; $m | Out-File -Encoding utf8 $mp
$r = Invoke-ValidatorWithShim -Fixtures $s.Fixtures -Recycle $s.Recycle -Manifest $mp
Assert-Case 'C. Repaired fixture, empty recycle bin -> FAIL (exit 1)' ($r.ExitCode -eq 1) $r.Output

# E. Two fixtures sharing a basename but different bytes.
$s = New-Scenario 'same-basename-different-bytes'
$aBytes = [byte[]](1..64)
$bBytes = [byte[]](100..163)
Write-BytesTo (Join-Path $s.Recycle "bin-a\dup.mkv") $aBytes
Write-BytesTo (Join-Path $s.Recycle "bin-b\dup.mkv") $bBytes
Write-BytesTo (Join-Path $s.Fixtures "libA\dup.mkv") ([byte[]](5..69))
Write-BytesTo (Join-Path $s.Fixtures "libB\dup.mkv") ([byte[]](5..69))
$scratchA = Join-Path $s.Root 'a.bin'; Write-BytesTo $scratchA $aBytes; $ha = Get-Sha256Hex $scratchA
$scratchB = Join-Path $s.Root 'b.bin'; Write-BytesTo $scratchB $bBytes; $hb = Get-Sha256Hex $scratchB
$m = @{ fixtures = @(
    @{ name = 'libA\dup.mkv'; targetRung = 1; postExists = 'libA\dup.mkv'; sourceSha256 = $ha },
    @{ name = 'libB\dup.mkv'; targetRung = 1; postExists = 'libB\dup.mkv'; sourceSha256 = $hb }
) } | ConvertTo-Json -Depth 6
$mp = Join-Path $s.Root 'manifest.json'; $m | Out-File -Encoding utf8 $mp
$r = Invoke-ValidatorWithShim -Fixtures $s.Fixtures -Recycle $s.Recycle -Manifest $mp
Assert-Case 'E. Two fixtures share basename but distinct hashes both resolve -> exit 0' ($r.ExitCode -eq 0) $r.Output

# Report
$fail = @($Script:CaseResults | Where-Object { -not $_.Passed }).Count
$pass = @($Script:CaseResults | Where-Object { $_.Passed }).Count
Write-Host ""
Write-Host "Validator self-test summary: $pass passed, $fail failed"
if ($fail -gt 0) { exit 1 } else { exit 0 }
