# self-tests.ps1 - isolated harness truth-tests.
#
# These load the framework module directly and exercise its real accounting +
# exit-decision function (Get-RunVerdict) with stubbed external dependencies
# (browser, network, file operations). Never stub the assertion itself.
#
# Runs as a child process; nonzero exit = at least one self-test failed. The
# release run must invoke this before claiming the harness is trustworthy.
#
# Table (matches plan section 00-B):
#   1. Browser exit 1                      -> UI tests FAIL
#   2. Browser empty DOM                   -> assertion FAIL
#   3. Phase throws before first test      -> verdict ERROR
#   4. Unknown phase id                    -> run refused (exit 3)
#   5. Required phase all skipped          -> verdict FAIL (with reason)
#   6. Teardown throws after failing case  -> both failures retained, exit 1
#   7. Target is nonloopback / wrong port  -> refused before mutation
#   8. Clean successful stub run           -> verdict PASS

[CmdletBinding()] param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 1.0

$TestRoot = Split-Path -Parent $PSCommandPath
$PluginRoot = Split-Path -Parent (Split-Path -Parent $TestRoot)

# Which PowerShell host is available for child-process invocations? On Windows
# with only Windows PowerShell 5.1, "powershell" exists. On Linux/macOS and on
# modern Windows CI runners, "pwsh" (PS 7) exists. Prefer pwsh when both are
# present; the self-tests do not depend on any 5.1-only behavior.
$Script:PsChild = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

# Minimal shared state so framework.ps1 helpers have somewhere to write.
# We DO NOT stub Get-RunVerdict, Record-PhaseException, Assert-SafeLiveTarget,
# Invoke-Test or Write-TestResult - those are the code under test.
function New-StubRunState {
    return [pscustomobject]@{
        RunId = 'self-test'
        StartedAtUtc = [DateTime]::UtcNow
        CurrentPhase = $null
        CurrentTest = $null
        PhasesRun = @()
        TestsPassed = 0
        TestsFailed = 0
        TestsSkipped = 0
        TestsErrored = 0
        TotalTests = 0
        PhaseTimings = @{}
        HaltOnFailure = $false
        JellyfinAuthHeader = $null
        JellyfinUrl = 'http://localhost:8099'
        PhaseExceptions = @()
        PhaseCounters = @{}
    }
}

function Initialize-SelfTestScope {
    $Script:RunState = New-StubRunState
    $Script:LogPath      = [IO.Path]::GetTempFileName()
    $Script:JsonlPath    = [IO.Path]::GetTempFileName()
    $Script:StatusPath   = [IO.Path]::GetTempFileName()
    $Script:FailuresDir  = [IO.Path]::Combine([IO.Path]::GetTempPath(), "self-test-fail-" + [Guid]::NewGuid())
    New-Item -ItemType Directory -Force -Path $Script:FailuresDir | Out-Null
    $Script:SummaryPath  = [IO.Path]::GetTempFileName()
}

. (Join-Path $TestRoot 'lib\framework.ps1')

$Script:SelfTestResults = @()
function Assert-Condition {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    $Script:SelfTestResults += [pscustomobject]@{ Name = $Name; Passed = $Condition; Detail = $Detail }
    if ($Condition) {
        Write-Host "  [PASS] $Name"
    } else {
        Write-Host "  [FAIL] $Name -- $Detail"
    }
}

Write-Host "=== MediaDash harness self-tests ==="

# 1. Browser exit 1 (with success-looking output) -> the wrapper must throw.
Initialize-SelfTestScope
. (Join-Path $TestRoot 'phases\07-ui.ps1')

# Substitute Invoke-Browse for this test scope so no real browser is launched.
function Invoke-Browse {
    param([string]$Bin, [string[]]$BrowseArgs)
    return @{ Output = 'browser failed'; ExitCode = 1 }
}

$threw = $false; $msg = ''
try {
    Invoke-BrowseChecked -Bin 'stub' -BrowseArgs @('goto','http://x') | Out-Null
} catch [TestAssertionFailedException] {
    $threw = $true; $msg = $_.Exception.Message
} catch {
    $threw = $true
    $etype = $_.Exception.GetType().Name
    $emsg  = $_.Exception.Message
    $msg = "wrong exception type: $etype -- $emsg"
}
Assert-Condition '1a. Browser exit 1 -> Invoke-BrowseChecked throws' $threw $msg

# 2. Browser exit 0 but empty output -> -AllowEmpty:$false must fail.
function Invoke-Browse {
    param([string]$Bin, [string[]]$BrowseArgs)
    return @{ Output = ''; ExitCode = 0 }
}

$threw = $false
try {
    Invoke-BrowseChecked -Bin 'stub' -BrowseArgs @('js','x') | Out-Null
} catch [TestAssertionFailedException] {
    $threw = $true
}
Assert-Condition '2a. Empty browser output (default) -> assertion FAIL' $threw ''

$didNotThrow = $true
try {
    Invoke-BrowseChecked -Bin 'stub' -BrowseArgs @('goto','x') -AllowEmpty | Out-Null
} catch {
    $didNotThrow = $false
}
Assert-Condition '2b. Empty browser output with -AllowEmpty -> returns' $didNotThrow ''

# 3. Phase throws before any test recorded -> verdict ERROR.
Initialize-SelfTestScope
Record-PhaseException -Phase '02 scanners' -ErrorRecord (New-Object System.Management.Automation.ErrorRecord (
    (New-Object System.InvalidOperationException 'scanner crash'), 'stub', 'NotSpecified', $null))
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '3a. Phase throws -> verdict ERROR' ($verdict.Verdict -eq 'ERROR') "got $($verdict.Verdict) -- $($verdict.Reason)"
Assert-Condition '3b. Phase throws -> exit code 2' ($verdict.ExitCode -eq 2) "got $($verdict.ExitCode)"

# 4. Unknown phase id -> runner refuses (exit 3).
$runner = Join-Path $TestRoot 'run-full-tests.ps1'
$childOut = & $Script:PsChild -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $runner -OnlyPhases 'nope-does-not-exist' 2>&1 | Out-String
$childExit = $LASTEXITCODE
Assert-Condition '4a. Unknown phase id -> exit 3' ($childExit -eq 3) "child exit was $childExit"
Assert-Condition '4b. Unknown phase id -> clear message' ($childOut -match "unknown phase 'nope-does-not-exist'") "output: $($childOut.Substring(0,[Math]::Min(200,$childOut.Length)))"

# 5. Required phase produced no results -> verdict FAIL.
Initialize-SelfTestScope
$Script:RunState.PhasesRun = @('preflight','ui')
$Script:RunState.TotalTests = 1
$Script:RunState.TestsPassed = 1
$Script:RunState.PhaseCounters['preflight'] = 1
$Script:RunState.PhaseCounters['ui'] = 0
$verdict = Get-RunVerdict -RunState $Script:RunState -RequiredPhasesRun @('preflight','ui')
Assert-Condition '5a. Required phase with 0 results -> FAIL' ($verdict.Verdict -eq 'FAIL') "got $($verdict.Verdict) -- $($verdict.Reason)"
Assert-Condition '5b. Required phase FAIL reason mentions the phase name' ($verdict.Reason -match 'ui') $verdict.Reason

# 6. Teardown throws after a failing case -> both failures retained, exit 1.
Initialize-SelfTestScope
$Script:RunState.CurrentPhase = '02 scanners'
Write-TestResult -Phase '02 scanners' -Name 'stub-fail' -Result 'FAIL' -DurationMs 1 -Notes 'stub'
Record-PhaseException -Phase '99 teardown' -ErrorRecord (New-Object System.Management.Automation.ErrorRecord (
    (New-Object System.IO.IOException 'teardown crash'), 'stub', 'NotSpecified', $null))
Assert-Condition '6a. Teardown exception did not erase prior FAIL' ($Script:RunState.TestsFailed -ge 1) "TestsFailed=$($Script:RunState.TestsFailed)"
Assert-Condition '6b. Teardown exception recorded as ERROR' ($Script:RunState.TestsErrored -ge 1) "TestsErrored=$($Script:RunState.TestsErrored)"
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '6c. Prior FAIL + teardown ERROR -> verdict ERROR (higher severity wins)' ($verdict.Verdict -eq 'ERROR') "got $($verdict.Verdict) -- $($verdict.Reason)"

# 7. Target-safety guard refuses non-loopback / wrong port / escape.
$refused = $false
try { Assert-SafeLiveTarget -Url 'http://192.168.1.117:8096' } catch { $refused = $true }
Assert-Condition '7a. Non-loopback target refused' $refused ''

$refused = $false
try { Assert-SafeLiveTarget -Url 'http://localhost:8096' } catch { $refused = $true }
Assert-Condition '7b. Loopback but wrong port refused (8096 is prod)' $refused ''

$refused = $false
try { Assert-SafeLiveTarget -Url 'http://localhost:8099' -AllowedFixturePaths @('C:\dev\mediadash-fixtures') -FixturePath 'C:\Windows\notepad.exe' } catch { $refused = $true }
Assert-Condition '7c. Loopback OK but fixture outside allowlist refused' $refused ''

$didNotThrow = $true
try { Assert-SafeLiveTarget -Url 'http://localhost:8099' -AllowedFixturePaths @([IO.Path]::GetTempPath()) -FixturePath ([IO.Path]::Combine([IO.Path]::GetTempPath(),'x.mkv')) } catch { $didNotThrow = $false }
Assert-Condition '7d. Loopback + fixture inside allowlist accepted' $didNotThrow ''

# 8. Clean stub run -> verdict PASS.
Initialize-SelfTestScope
$Script:RunState.CurrentPhase = '02 scanners'
Write-TestResult -Phase '02 scanners' -Name 'stub-pass' -Result 'PASS' -DurationMs 1
$verdict = Get-RunVerdict -RunState $Script:RunState
Assert-Condition '8a. All PASS -> verdict PASS' ($verdict.Verdict -eq 'PASS') "got $($verdict.Verdict) -- $($verdict.Reason)"
Assert-Condition '8b. All PASS -> exit 0' ($verdict.ExitCode -eq 0) "got $($verdict.ExitCode)"

# Report
$totalFail = @($Script:SelfTestResults | Where-Object { -not $_.Passed }).Count
$totalPass = @($Script:SelfTestResults | Where-Object { $_.Passed }).Count
Write-Host ""
Write-Host "Self-test summary: $totalPass passed, $totalFail failed"
if ($totalFail -gt 0) { exit 1 } else { exit 0 }
