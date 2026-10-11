# Compatibility entry point. The former script emptied a shared recycle bin and
# enabled every library; use the isolated, evidence-producing runner instead.
[CmdletBinding()]
param(
    [string]$Python = 'python',
    [string]$Ffmpeg = '',
    [string]$JellyfinUrl = 'http://localhost:8099',
    [string]$AdminUser = 'test',
    [string]$DataDir = "$env:LOCALAPPDATA\jellyfin-v10",
    [string]$OutputDir = '',
    [switch]$ConfirmLive,
    [switch]$HoldExistingQueue
)
$ErrorActionPreference = 'Stop'
if (-not $ConfirmLive) { throw 'Specify -ConfirmLive to run isolated live tests. See tools/repair-test/README.md.' }
if (-not $Ffmpeg) { $Ffmpeg = "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe" }
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot ('runs\' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)) }
$runner = Join-Path $PSScriptRoot 'run_live.py'
$queueArgs = @()
if ($HoldExistingQueue) { $queueArgs += '--hold-existing-queue' }
& $Python -B $runner @queueArgs --confirm-live --url $JellyfinUrl --username $AdminUser --ffmpeg $Ffmpeg --data-dir $DataDir --output $OutputDir
exit $LASTEXITCODE
