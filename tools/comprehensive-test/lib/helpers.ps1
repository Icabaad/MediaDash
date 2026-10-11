# helpers.ps1 — Jellyfin auth, HTTP wrappers, MediaDash-specific helpers.
# Consumed via dot-source from run-full-tests.ps1.

# ═════════════════════════════════════════════════════════════════════════════
# Auth
# ═════════════════════════════════════════════════════════════════════════════

function Connect-JellyfinAdmin {
    param()
    $url = $Script:RunState.JellyfinUrl
    $body = @{ Username = $Script:RunState.AdminUser; Pw = $Script:RunState.AdminPass } | ConvertTo-Json -Compress
    $bootstrapHeader = 'MediaBrowser Client="mdtest", Device="ci", DeviceId="ci-run", Version="1"'
    $r = Invoke-RestMethod -Method Post -Uri "$url/Users/AuthenticateByName" -ContentType application/json `
        -Body $body -Headers @{ Authorization = $bootstrapHeader } -TimeoutSec 30
    $token = $r.AccessToken
    $userId = $r.User.Id
    $Script:RunState.JellyfinToken = $token
    $Script:RunState.JellyfinUserId = $userId
    $Script:RunState.JellyfinAuthHeader = 'MediaBrowser Token="' + $token + '", Client="mdtest", Device="ci", DeviceId="ci-run", Version="1"'
    Write-Log "Auth OK — user $userId"
}

# ═════════════════════════════════════════════════════════════════════════════
# Invoke-JfApi — universal Jellyfin/MediaDash API wrapper.
# ═════════════════════════════════════════════════════════════════════════════

function Invoke-JfApi {
    param(
        [Parameter(Mandatory)][ValidateSet("GET","POST","PUT","DELETE","PATCH")][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body = $null,
        [string]$ContentType = "application/json",
        [int]$TimeoutSec = 30,
        [switch]$Raw,
        [switch]$IgnoreErrors
    )
    $url = ($Script:RunState.JellyfinUrl.TrimEnd('/')) + $Path
    $headers = @{}
    if ($Script:RunState.JellyfinAuthHeader) {
        $headers.Authorization = $Script:RunState.JellyfinAuthHeader
    }
    $argList = @{
        Method = $Method
        Uri = $url
        Headers = $headers
        TimeoutSec = $TimeoutSec
    }
    if ($null -ne $Body) {
        if ($Body -is [string]) { $argList.Body = $Body } else { $argList.Body = ($Body | ConvertTo-Json -Depth 10 -Compress) }
        $argList.ContentType = $ContentType
    }
    try {
        if ($Raw) {
            return Invoke-WebRequest @argList -UseBasicParsing
        } else {
            return Invoke-RestMethod @argList
        }
    } catch {
        if ($IgnoreErrors) { return $null }
        throw
    }
}

# ═════════════════════════════════════════════════════════════════════════════
# Library management
# ═════════════════════════════════════════════════════════════════════════════

function Add-JellyfinLibrary {
    param([string]$Name, [string]$Type, [string]$Path)
    $q = "name=$([Uri]::EscapeDataString($Name))&collectionType=$Type&paths=$([Uri]::EscapeDataString($Path))&refreshLibrary=false"
    $body = @{ LibraryOptions = @{} } | ConvertTo-Json -Compress
    Invoke-JfApi -Method POST -Path "/Library/VirtualFolders?$q" -Body $body | Out-Null
    Write-Log "Added library: $Name ($Type) -> $Path"
    $Script:RunState.AddedLibraryIds += $Name
}

function Remove-JellyfinLibrary {
    param([string]$Name)
    $q = "name=$([Uri]::EscapeDataString($Name))&refreshLibrary=false"
    Invoke-JfApi -Method DELETE -Path "/Library/VirtualFolders?$q" -IgnoreErrors | Out-Null
    Write-Log "Removed library: $Name"
}

function Get-JellyfinLibraries {
    Invoke-JfApi -Method GET -Path "/Library/VirtualFolders"
}

function Wait-ForJellyfinScan {
    param([int]$TimeoutSec = 300)
    Start-MediaDashTask -Key 'RefreshLibrary' -Route '/Library/Refresh' -Wait -TimeoutSec $TimeoutSec
}

function Get-JellyfinItemCount {
    param([string]$IncludeItemTypes = "Movie")
    $q = "?Recursive=true&IncludeItemTypes=$IncludeItemTypes"
    $r = Invoke-JfApi -Method GET -Path "/Items$q"
    return [int]$r.TotalRecordCount
}

# ═════════════════════════════════════════════════════════════════════════════
# MediaDash plugin control
# ═════════════════════════════════════════════════════════════════════════════

function Get-MediaDashConfig {
    Invoke-JfApi -Method GET -Path "/Plugins/$($Script:RunState.PluginId.Replace('-',''))/Configuration"
}

function Set-MediaDashConfig {
    param([hashtable]$Patch)
    $cfg = Get-MediaDashConfig
    foreach ($k in $Patch.Keys) { $cfg | Add-Member -MemberType NoteProperty -Name $k -Value $Patch[$k] -Force }
    $json = $cfg | ConvertTo-Json -Depth 10 -Compress
    Invoke-JfApi -Method POST -Path "/Plugins/$($Script:RunState.PluginId.Replace('-',''))/Configuration" -Body $json | Out-Null
    # ConfigChangeRescanner has a 750ms debounce. Drain it before a subsequent
    # Reset/manual scan so that a queued config scan cannot invalidate the test.
    Start-Sleep -Milliseconds 1500
    Wait-For -TimeoutSec 600 -Description 'configuration rescan to drain' -Predicate {
        $tasks = Invoke-JfApi -Method GET -Path '/ScheduledTasks'
        $worker = $tasks | Where-Object Key -eq 'MediaDashScan' | Select-Object -First 1
        return ($worker -and $worker.State -eq 'Idle')
    } | Out-Null
}

function Save-MediaDashConfigSnapshot {
    if (-not $Script:RunState.OriginalConfigSnapshot) {
        $Script:RunState.OriginalConfigSnapshot = Get-MediaDashConfig
        Write-Log "Snapshotted original MediaDash config"
    }
}

function Restore-MediaDashConfigSnapshot {
    if ($Script:RunState.OriginalConfigSnapshot) {
        $json = $Script:RunState.OriginalConfigSnapshot | ConvertTo-Json -Depth 10 -Compress
        Invoke-JfApi -Method POST -Path "/Plugins/$($Script:RunState.PluginId.Replace('-',''))/Configuration" -Body $json | Out-Null
        Write-Log "Restored original MediaDash config"
    }
}

function Start-MediaDashTask {
    param([string]$Key, [string]$Route, [switch]$Wait, [int]$TimeoutSec)
    $before = $null
    if ($Wait) {
        $before = Wait-For -TimeoutSec $TimeoutSec -Description "$Key to be idle before starting" -Predicate {
            $tasks = Invoke-JfApi -Method GET -Path '/ScheduledTasks'
            $worker = $tasks | Where-Object Key -eq $Key | Select-Object -First 1
            if ($worker -and $worker.State -eq 'Idle') { return $worker }
            return $false
        }
    }
    Invoke-JfApi -Method POST -Path $Route | Out-Null
    if ($Wait) {
        # A fast task may finish between polls. A changed execution timestamp proves it ran;
        # observing Idle alone does not. Rejected requests and failed executions are failures.
        $finished = Wait-For -TimeoutSec $TimeoutSec -Description "$Key to complete a new execution" -Predicate {
            $tasks = Invoke-JfApi -Method GET -Path '/ScheduledTasks'
            $worker = $tasks | Where-Object Key -eq $Key | Select-Object -First 1
            if ($worker -and $worker.State -eq 'Idle' -and $worker.LastExecutionResult -and
                $worker.LastExecutionResult.EndTimeUtc -ne $before.LastExecutionResult.EndTimeUtc) { return $worker }
            return $false
        }
        if ($finished.LastExecutionResult.Status -ne 'Completed') {
            throw "$Key execution ended with $($finished.LastExecutionResult.Status)"
        }
    }
}

function Start-MediaDashScan {
    param([switch]$Wait, [int]$TimeoutSec = 600)
    Start-MediaDashTask -Key 'MediaDashScan' -Route '/MediaDash/Scan' -Wait:$Wait -TimeoutSec $TimeoutSec
}

function Start-MediaDashFix {
    param([switch]$Wait, [int]$TimeoutSec = 1800)
    Start-MediaDashTask -Key 'MediaDashFix' -Route '/MediaDash/Fix' -Wait:$Wait -TimeoutSec $TimeoutSec
}
function Get-MediaDashIssues {
    param([string]$Type = $null, [string]$Status = $null)
    $q = @()
    if ($Type) { $q += "type=$Type" }
    if ($Status) { $q += "status=$Status" }
    $qs = if ($q.Count -gt 0) { "?" + ($q -join "&") } else { "" }
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/Issues$qs"
    # Endpoint returns a bare JSON array. Force to array AND prevent PS return
    # flattening via the leading comma — otherwise `return @()` collapses to
    # $null in the caller and .Count throws under Set-StrictMode -Version 2.
    $arr = if ($null -eq $r) { @() } else { @($r) }
    ,$arr
}

function Approve-MediaDashIssue {
    param([long]$IssueId)
    Invoke-JfApi -Method POST -Path "/MediaDash/Issues/$IssueId/Approve" | Out-Null
}

function Dismiss-MediaDashIssue {
    param([long]$IssueId)
    Invoke-JfApi -Method POST -Path "/MediaDash/Issues/$IssueId/Dismiss" | Out-Null
}

function Get-MediaDashRecycleBinSummary {
    # Returns { FileCount, SizeBytes, IsEmptying, EmptyingDone, EmptyingTotal }
    Invoke-JfApi -Method GET -Path "/MediaDash/RecycleBin"
}

function Get-MediaDashRecycleBinItems {
    # Returns bare array of recycle-bin entries.
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/RecycleBin/Items"
    $arr = if ($null -eq $r) { @() } else { @($r) }
    ,$arr
}

# Backward-compat alias — callers used $bin.Items before. Now returns the items array directly.
# For summary access, callers should switch to Get-MediaDashRecycleBinSummary.
function Get-MediaDashRecycleBin {
    return [PSCustomObject]@{
        Items = Get-MediaDashRecycleBinItems
        Summary = Get-MediaDashRecycleBinSummary
    }
}

function Get-MediaDashHistory {
    param([int]$Limit = 500)
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/History?limit=$Limit"
    if ($null -eq $r) { return [PSCustomObject]@{ Items = @() } }
    # Return a wrapper so existing $history.Items call sites keep working
    return [PSCustomObject]@{ Items = @($r) }
}

function Get-MediaDashDiagnostics {
    # Real route is /MediaDash/Errors — /Diagnostics doesn't exist. Returns bare array.
    $r = Invoke-JfApi -Method GET -Path "/MediaDash/Errors" -IgnoreErrors
    $arr = if ($null -eq $r) { @() } else { @($r) }
    ,$arr
}

function Get-MediaDashStatus {
    Invoke-JfApi -Method GET -Path "/MediaDash/Status"
}

# ═════════════════════════════════════════════════════════════════════════════
# File helpers
# ═════════════════════════════════════════════════════════════════════════════

function Copy-FixtureFile {
    param([string]$Src, [string]$Dst)
    $dir = Split-Path -Parent $Dst
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    Copy-Item -LiteralPath $Src -Destination $Dst -Force
    $Script:RunState.CreatedFixturePaths += $Dst
}

function Get-JellyfinFfmpegPath {
    # The bundled ffmpeg that MediaDash's FfmpegExecutor uses at runtime.
    $candidates = @(
        "$env:USERPROFILE\Downloads\jellyfin_10.11.11-amd64\jellyfin\ffmpeg.exe",
        "$env:LOCALAPPDATA\jellyfin\ffmpeg\jellyfin-ffmpeg.exe",
        "$env:ProgramFiles\Jellyfin\Server\ffmpeg.exe"
    )
    foreach ($c in $candidates) { if (Test-Path -LiteralPath $c) { return $c } }
    return $null
}
