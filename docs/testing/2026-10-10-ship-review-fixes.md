# Ship-review fixes — 10 October 2026

The five findings from the local ship-readiness review were fixed in the working tree. Existing unrelated changes were preserved. Nothing was committed, pushed, deployed or published.

## Changes and regression coverage

- **Original tracks during re-encoding:** `TranscodeFixer` now uses `TrackFixer.ComputeRemovableIndexes` for audio/subtitle cleanup. This shares original-language resolution, unknown-identity safety, last-audio protection and hearing-impaired retention. Both hardware and software argument builders receive the current Jellyfin item for inherited original-language metadata. Tests cover flagged original tracks, metadata originals, unknown identity and hearing-impaired subtitles.
- **Retained-track verification:** Re-encoding verifies the planned audio/subtitle inventory, languages and original/hearing-impaired flags before replacement. Audio codec changes are allowed; subtitle codecs must still match because subtitles are copied. Tests reject missing retained tracks and changed subtitle codecs. Existing track-remux verification continues requiring exact codec matches.
- **Commit-time settings and consent:** Re-encoding refuses replacement when the current fixer is Off, DetectOnly, on the disabled list, dry-run or protected. It refreshes same-path companion approvals, item metadata and track-removal decisions immediately before replacement. Tracks newly protected by a language choice, unresolved Original identity or revoked cleanup permission cause refusal. Tests cover disabled modes/list, changed allowlists, unknown original identity and revoked companion approval.
- **Hidden/system links during cross-volume moves:** The reparse-point walk now includes hidden/system entries and refuses inaccessible enumeration. Inventory enumeration also refuses inaccessible entries. Owned Windows junction fixtures verify refusal before copying, preserving the source, junction and junction target; on other operating systems the same tests use directory symlinks.
- **Partial repair totals:** The SQL aggregation includes successful `partially repaired ...` history entries. A real temporary SQLite test checks lifetime totals, the 30-day window, rung totals, and exclusion of dry runs/failures.
- **UI lifecycle checks:** The asset assertion expects 47 picker entries and explicitly requires Original language. The lifecycle suite now runs normally from disk.

Plain-language bug-fix entries were added to `CHANGELOG.md` under 1.0.8.0.

## Validation

- Focused C# regression run: **99 passed, 0 failed, 0 skipped**.
- Full Release C# suite: **1,499 passed, 0 failed, 1 skipped**.
- Dashboard UI regression suite: **25 passed**.
- Dashboard UI lifecycle suite: **all assertions passed**, using the unchanged test file from disk after the fix.
- Release publish: **passed**, written to `C:\dev\ship-review-20261010\fixed-publish`.
- Whitespace check of changed tracked implementation/test files: **passed**.

Full-suite result: `Jellyfin.Plugin.MediaDash.Tests/TestResults/ship-fixes-20261010.trx`.

Published DLL SHA-256: `B623261C2DD742EAC220B57AA94985271F220FF42DC28101CAB8C39FF55FD9B0`.

## Remaining release boundary

**Do-not-push for release yet.** These fixes close the five reported code/test findings; they do not certify the complete concurrent release bundle. Fresh detect → approve → fix → rescan/history → playback/seek → restore acceptance against the final DLL is still outstanding, as are the supported-host and interleave resource/client checks described in the initial review.

The existing `CapturesSourceIdentityBeforeConversion` integration placeholder remains skipped. No fresh live Jellyfin/browser acceptance, Linux/Docker/macOS execution, GPU test, crash recovery or disk-full injection was performed in this fix pass.
