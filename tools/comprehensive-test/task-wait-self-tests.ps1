$ErrorActionPreference='Stop'
. "$PSScriptRoot/lib/helpers.ps1"
function Wait-For {
    param([scriptblock]$Predicate,[int]$TimeoutSec,[string]$Description)
    for($i=0;$i -lt 3;$i++) { $result=& $Predicate; if($result) {return $result} }
    throw "Timeout: $Description"
}
function Invoke-JfApi {
    param($Method,$Path)
    if($Method -eq 'POST') {
        if($script:scenario -eq 'rejected') {throw 'HTTP 409'}
        $script:posted=$true
        return
    }
    $stamp=if($script:posted -and $script:scenario -ne 'never-ran') {'new'} else {'old'}
    $status=if($script:scenario -eq 'failed') {'Failed'} else {'Completed'}
    return [pscustomobject]@{Key='MediaDashScan';State='Idle';LastExecutionResult=[pscustomobject]@{EndTimeUtc=$stamp;Status=$status}}
}
foreach($scenario in @('fast-completion','never-ran','failed','rejected')) {
    $script:scenario=$scenario; $script:posted=$false; $failed=$false
    try {Start-MediaDashScan -Wait -TimeoutSec 1} catch {$failed=$true}
    if($failed -ne ($scenario -ne 'fast-completion')) {throw "Incorrect verdict for $scenario"}
    "PASS: $scenario"
}
