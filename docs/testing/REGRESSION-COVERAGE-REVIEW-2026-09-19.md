# Regression coverage review — 2026-09-19

The current material is broad, but is not yet a dependable release gate. The unit
suite is healthy; several live-test checks can pass without proving their claim,
and the planned features need explicit acceptance cases before implementation.

## Evidence and scope

- Reviewed local MediaDash working tree based on HEAD
  `4809c3dd890aa4e6c8f6595252ef59fc1aadee54`, including Claude's uncommitted fixes
  and new tests. This is not a review of HEAD alone.
- Ran `dotnet test Jellyfin.Plugin.MediaDash.sln --nologo --logger
  "trx;LogFileName=coverage-review.trx" --results-directory artifacts/test-coverage-review`:
  **998 passed, 0 failed, 0 skipped**, Windows, Debug/net9.0, 16 seconds test time.
  Local result: `artifacts/test-coverage-review/coverage-review.trx`.
- Read CI, test project, representative regression tests, E2E chapters/index,
  comprehensive harness, repair validators/torture scoring and all 29 unfinished
  roadmap cards. The board contains 70 cards; 2 unfinished cards are superseded.
- Proved three UI false passes using the actual phase script with a fake browser
  returning exit code 1 and `error: browser unavailable`. Login, Issues chips and
  Recycle bin checks all returned normally. The probe stubbed sleep/logging and
  invoked only those blocks; it made no HTTP requests or browser interactions.
- No live destructive tests, deployment, Linux run, host upgrade or actual Jellyfin
  playback was performed. No production source, existing tests or CI were changed.
  A unit pass is not a certification of the installed plugin or release package.

## Findings, in priority order

### TCR-01 — P1: repair checks can pass despite missing recovery evidence

`tools/repair-test/validate-outputs.ps1:26` indexes recycled files by basename,
so an old run or another folder with the same name can satisfy the check. At
line 68 a missing recycled original becomes WARN, and line 92 exits successfully.
The negative control is checked for existence, not unchanged bytes. These checks
cannot establish that the original survived the current repair.

Required regression: seed two different originals with the same basename and a
stale bin entry. Record source SHA-256, full original path and run start before
repair. Require the current operation's manifest plus matching hash; remove its
bin entry and prove validation exits nonzero. Mutate a healthy control in place
and prove it fails. Restore and compare bytes, not just filenames. For deliberate
permanent disposal, declare that policy in the case rather than accepting missing
recovery evidence generally.

### TCR-02 — P1: UI checks report success when the browser fails

`tools/comprehensive-test/phases/07-ui.ps1:46`, `:96` and `:103` contain the three
reproduced false passes. `Invoke-Browse` returns an ExitCode at line 32, but these
callers ignore it. Login does not assert authentication, Issues only logs a count,
and Recycle bin computes errors without asserting them.

Require browser exit success and an observable state assertion in each case:
authenticated page, active Issues panel with expected fixture chips, and rendered
Recycle bin panel with no errors. A missing browser is BLOCKED for a release UI
gate. Harness self-tests must simulate browser failure, missing tab, empty result
and console exception and require non-success in each case. Current automation
also lacks the phone/light/dark matrix in RG-10 below.

### TCR-03 — P1: phase-level exceptions are not counted as failures

`tools/comprehensive-test/run-full-tests.ps1:187` logs exceptions thrown outside
`Invoke-Test` without incrementing TestsErrored. The exit decision at line 215
uses only TestsFailed/TestsErrored. A phase can abort without a recorded failing
test, allowing a zero exit if the other phases succeed. Phase selection also
allows preflight/setup to be omitted; an invalid selection can produce no useful
coverage. This is a static finding, not an executed full-run reproduction.

Record every phase exception as ERROR; validate phase identifiers and required
preconditions; make release mode reject absent required phases, all-skipped runs
and aborted runs. Self-test a phase throwing before its first test, an unknown
phase ID, missing browser and failed teardown. Each must return nonzero or an
explicit non-passing blocked status with artifacts.

### TCR-04 — P1: release automation covers only one OS and no live host

`.github/workflows/ci.yaml:14` runs only Ubuntu; line 23 runs unit tests only.
The test project references Jellyfin 10.11.8, so it cannot demonstrate that the
published plugin loads against both supported host lines. Windows-specific tests
such as the drive-root case in LibraryGuardTests return early on Linux.

`tools/integration-test.sh:33` pins one 10.11 host, mounts media read-only and
checks four minimum issue counts. It does not exercise repair, restore, upgrade,
or the second host line, and is not called by CI. Its startup wait at line 34 is
unbounded; scan polling has no explicit timeout-failure assertion after its loop.
It also uses port 8097, outside this review's authorised 8099 test target.

Add Windows/Ubuntu Release unit jobs with TRX artifacts. Add bounded disposable
install/upgrade smoke jobs for each supported host line, using the actual package
contents. Keep platform-specific assertions; do not remove them to make CI green.
Record macOS, Docker and network-storage coverage separately where not automated.
Bring the integration runner's target policy into agreement with local guidance
before running it here. These are requirements for i-108-cross-platform-verification,
not evidence that that card is complete.

### TCR-05 — P2: torture success is not proof of repair quality or playback

The scoring at the end of `tools/repair-test/torture-test.ps1` accepts recycling
as PASS for broken fixtures and checks existence for some untouched/renamed cases.
That is useful for disposal safety, but it can hide loss of repair capability.
`validate-outputs.ps1:61` also overstates strict decode as proof of Jellyfin direct
play. Client/container/host behavior still needs a real playback check.

Give each fixture an independent allowed outcome: must repair, rename only,
must remain untouched, or may recycle after an authorised failed repair. Pin
required streams, dispositions, duration, chapters and attachments as appropriate.
Run strict decode and a Jellyfin playback/seek check. A repair-required fixture
recycled intact must fail the repair case even if it passes the recovery case.

### TCR-06 — P2: helper tests leave orchestration and failure windows unproven

New tests cover useful logic: TranscodeFixerAuthorizationTests tests mappings,
SwapRecoveryHelperTests exercises recovery naming, and FileBrowserCrossDeviceMoveTests
uses real temporary files. The latter directly calls the fallback helper with
source and target under one temp directory: it does not force a genuine
cross-filesystem failure through the HTTP entry point.

Keep these tests. Add controlled integration/fault-injection cases for changes
between scan/approval/execution, partial copy, cancellation, unavailable mounts,
concurrent restore/purge and process interruption during a swap. Assert filesystem
bytes, DB status, history and user-facing outcome together; a helper result alone
does not prove the caller uses it. RG-01 through RG-08 specify the missing layers.

### TCR-07 — P2: old evidence and future plans are easy to mistake for coverage

`docs/testing/INDEX.md` has duplicate 02-fixers progress rows and approximate totals
from historical runs. Setup includes a real protected media file and old machine
state. Checked boxes and old pass counts are not evidence for this working tree.
Several roadmap notes are feature intentions without executable test oracles.

Use a fresh run record and the companion [roadmap acceptance matrix](ROADMAP-TEST-MATRIX.md).
Do not erase historical findings or retick old chapters to imply a new pass.
Every new defect needs a reproducer that fails before the fix and passes after it;
every completed feature needs linked evidence for its acceptance cases.

## Required regression cases for the current fixes

These are additional test specifications, **not claims that they passed**. Existing
test classes below are starting points; their presence does not cover every case.
Run destructive cases only on generated fixtures in an isolated, registered test
library on localhost:8099, with unrelated work disabled and fixture ownership
checked before approval. Never bulk-approve the shared library's entire queue.

| ID | Existing starting points | Required test and observable result |
|---|---|---|
| RG-01 Recovery/swap | SwapRecoveryHelperTests, LibraryGuardTests, PlayabilityFixerRepairTests | With recycle and permanent modes, inject failure before disposal, after disposal and before final rename; also fail recovery rename. Run cleanup and restart. At least one byte-verified recovery copy must remain and its actual path must be reported. No success history for a failed swap. |
| RG-02 Path boundaries | LibraryGuardTests, CopyDescendantGuardTests, FileBrowserCrossDeviceMoveTests | Exercise API entry points with sibling-prefix paths, `..`, symlink/junction escape, Windows drive root/UNC, Linux case differences, directory into descendant and genuine cross-volume copy. Inject disk full/cancel/target collision. Outside sentinel bytes and preexisting destinations remain unchanged; incomplete copy never deletes source. |
| RG-03 Restore claims | RestoreClaimTests, DirectoryRestoreTests, RestoreSuffixedProtectionTests | Race two restore requests and restore versus purge for one entry, including directories and occupied destination names. Exactly one claim wins, every restored byte matches, no entry is lost; suffixed restored paths stay protected after a scan and restart. |
| RG-04 Consent and stale work | BulkApproveConsentTests, AutoQueueRollbackScopeTests, TranscodeFixerAuthorizationTests, FixTaskTranscodeCompanionTests | Queue audio+subtitle+quality, then disable each type or change mode/library before execution. Disallowed operations never run; allowed companion work remains. Replace the source after scan and use an indeterminate probe. Preserve the changed source, report deferral/refusal and do not record false success. Re-enable and prove eligible work resumes. |
| RG-05 Stream integrity | TrackSafetyTests, TrackFixerMp4MuxerAudioTests, TrackFixerMp4MuxerSubtitleTests, CodecDecisionParityTests | Real MP4/M4V/MKV fixtures with multiple audio languages, forced/SDH/commentary, image/text subtitles, attached pictures, tmcd and dual-video DoVi. Include unknown/empty codec names. Compare before/after inventories and decode; preserve the last real video/audio and all tracks not explicitly authorised for removal. Unsupported preservation fails closed. |
| RG-06 Probe semantics | PlayabilityFixerRevalidationResultTests, BookProbeServiceTests, ComicProbeServiceTests | Healthy, truncated, unreadable, locked, DRM, extension mismatch and timeout cases must remain distinct through scanner, approval, fixer and history. Timeout is not corruption. DRM never reaches destructive repair. Rename collision preserves both files; successful rename preserves the source hash and produces playable output. |
| RG-07 DB/HTTP contracts | MalformedTimestampTests, OutOfRangeEnumHydrationTests, MediaDashDbTests, IssueRevertTests | Upgrade copied historical schemas with good and malformed rows; reopen twice. Test invalid GUID/enum/time/JSON roots through list/status/approve/revert routes. Good rows survive, bad rows are diagnosed without HTTP 500. Race approve/dismiss/revert; all outcomes follow the shared state machine. Include locked DB and failed migration recovery. |
| RG-08 Scheduling/space | FreeSpaceGuardTests, FixTaskDiskFullTests, FixTaskWindowTests, FixTaskSubtitleQuotaTests | Below/exactly/above space threshold on source, output and plugin-data volumes; probe failure; disk fills after preflight; cancellation; active playback; midnight window; quota and locked file. Failed/deferrable work stays recoverable and one blocked item does not starve others. Persist disable/re-enable across restart. |
| RG-09 SMART/diagnostics | SmartHealthProbeParseSafetyTests, DiagnosticsTests, BucketReasonContractTests | Run malformed smartctl output and every relevant exit-bit combination through scheduling. Disabled SMART launches no process and purges only SMART diagnostics; re-enable resumes. Repeated failures deduplicate, unrelated failures remain, and technical details survive API-to-UI rendering. |
| RG-10 Responsive UI | 07-config-ui.md, comprehensive phase 07 | 360/390/430 px phones plus 1280/1920 px desktops, light and dark. Verify every tab, keyboard focus, long filenames/localised labels, empty/loading/error states, filename-only History/Bin search, technical expander, 999+ counts and disable/re-enable controls. No clipped actions, unwanted horizontal overflow or raw exceptions in primary messages. Capture screenshots and assert actual interactions. |
| RG-11 Authentication | 03-api.md, comprehensive phase 06 | Enumerate current routes from local OpenAPI. Anonymous, ordinary user and admin requests must follow elevation policy. For each mutating route verify denied requests leave DB and fixture hashes unchanged. Cover expired credentials and library-root denial; status code alone is insufficient for mutations. |
| RG-12 Package and playback | CI, integration-test.sh, repair-test | Install the same locally built package on each supported host line; upgrade a prior install preserving config/DB. Check plugin load, DI, bundled dependencies, scan, dry-run, real fix, restore and playback at start/middle/end. Record host/package hashes and ffmpeg version. Decode success and same-volume copy are separate evidence from playback and cross-volume behavior. |

## Release evidence contract

For each run record: run ID/time, commit **and working-tree diff identity**, package
SHA-256, OS/filesystem, Jellyfin and ffmpeg versions, fixture generator/manifest,
selected phases, and pass/fail/blocked/not-run counts. Record source hashes before
mutation, observed output/stream inventory, relevant HTTP response, DB/history
outcome and restore hash. Save logs/screenshots with sensitive values removed.
Retain a fresh result directory per run; do not mix old bin entries into evidence.

Use these lanes:

1. Every code change: Windows and Ubuntu build/unit checks, plus the reproducer for
   the changed behavior. This review ran only the local Windows unit lane.
2. Every media/state change: affected RG integration cases on generated fixtures,
   including failure/negative controls and recovery. Unit success is insufficient.
3. Every UI change: RG-10 and API error rendering. Browser unavailable means blocked.
4. Before release: RG-01 through RG-12 where applicable, both host lines, clean
   install/upgrade and package identity. Any applicability exception needs a written
   reason. Safety failures and missing required evidence block readiness.

First repair TCR-01/02/03 so the harness cannot produce false confidence, then
enforce TCR-04 and add the operation-level cases in TCR-05/06. The new matrix closes
the missing **test specification** layer; it does not implement those automated
tests or establish release readiness. **Do-not-push based on this review alone.**
