# 99-teardown.ps1 — clean up after the run so the box is in a known state.
# Always runs (even if earlier phases errored). Best-effort — every step
# swallows its own errors so one broken cleanup step doesn't hide others.

function Invoke-Phase-Teardown {
    Start-Phase "Teardown — restore config, empty test recycle bin, remove test libraries"

    Invoke-Test "Restore MediaDash config to pre-run snapshot" {
        try {
            Restore-MediaDashConfigSnapshot
        } catch {
            Write-Log "    warn: config restore failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Empty the recycle bin of any test-created entries" {
        try {
            $items = Get-MediaDashRecycleBinItems
            $testEntries = @($items | Where-Object {
                $_.OriginalPath -like "*mediadash-fixtures*" -or
                $_.OriginalPath -like "*_test-scratch*"
            })
            if ($testEntries.Count -gt 0) {
                # External audit P2 #10: /MediaDash/RecycleBin/Items/Delete expects BinDeleteRequest
                # with a `BinPath` string (see Api/BinDeleteRequest.cs). The previous payload sent
                # `HistoryIds` which the controller discards; the endpoint responded 400, -IgnoreErrors
                # hid the failure, and teardown reported entries cleared while the bin still held them.
                $cleared = 0
                $failed = 0
                foreach ($e in $testEntries) {
                    $binPath = $e.BinPath
                    if (-not $binPath) {
                        $failed++
                        continue
                    }

                    $resp = Invoke-JfApi -Method POST -Path "/MediaDash/RecycleBin/Items/Delete" -Body @{ BinPath = $binPath } -IgnoreErrors
                    if ($null -ne $resp) {
                        $cleared++
                    } else {
                        $failed++
                    }
                }

                # Verify by re-listing: anything we targeted that still shows up is a real failure.
                $remaining = @((Get-MediaDashRecycleBinItems) | Where-Object { $_.OriginalPath -like "*mediadash-fixtures*" -or $_.OriginalPath -like "*_test-scratch*" })
                if ($remaining.Count -eq 0) {
                    Write-Log "    cleared $cleared test recycle-bin entries"
                } else {
                    Write-Log "    warn: $($remaining.Count) test entries still in bin after delete (sent=$($testEntries.Count), accepted=$cleared, rejected=$failed) — pollutes later runs"
                }
            }
        } catch {
            Write-Log "    warn: recycle bin cleanup failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Remove test Jellyfin libraries added during setup" {
        foreach ($libName in @("MediaDash Test", "MediaDash Scratch")) {
            try {
                Remove-JellyfinLibrary $libName
            } catch {
                Write-Log "    warn: could not remove $libName — $($_.Exception.Message)"
            }
        }
    }

    Invoke-Test "Remove scratch fixture directory" {
        try {
            Remove-ScratchFixtures
        } catch {
            Write-Log "    warn: scratch cleanup failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Fixture integrity check — report what the run modified" {
        try {
            $changes = Test-FixtureIntegrity -FixturesRoot $Script:RunState.FixturesRoot
            if ($changes.Count -eq 0) {
                Write-Log "    all fixture files match backup manifest (no modifications)"
            } else {
                Write-Log "    $($changes.Count) fixture file(s) changed during the run:"
                foreach ($c in $changes) { Write-Log "      [$($c.kind)] $($c.path)" }
                # Also write to an artifact so it's in the summary
                $artifactPath = Join-Path $Script:ArtifactsDir "fixture-changes.json"
                $changes | ConvertTo-Json -Depth 6 | Set-Content -Path $artifactPath -Encoding utf8
                Write-Log "    → $artifactPath"
            }
        } catch {
            Write-Log "    warn: integrity check failed — $($_.Exception.Message)"
        }
    }

    Invoke-Test "Verify no MediaDash issues remain in DB (informational)" {
        try {
            $issues = Get-MediaDashIssues
            Write-Log "    issues remaining in DB after teardown: $($issues.Count) (informational only)"
        } catch {
            Write-Log "    warn: issue count check failed — $($_.Exception.Message)"
        }
    }
}
