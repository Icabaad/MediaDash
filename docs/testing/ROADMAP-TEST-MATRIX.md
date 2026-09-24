# Roadmap acceptance test matrix

Reviewed 2026-09-19 against `tools/roadmap/roadmap.json`: 70 cards, 29 unfinished,
including 2 superseded. Each unfinished card appears exactly once below.
These are planned acceptance tests, not completed tests or implementation claims.
See [current coverage review](REGRESSION-COVERAGE-REVIEW-2026-09-19.md) for RG cases,
existing test starting points, harness defects and the release evidence contract.

Before marking a card complete, link its implemented tests and current run evidence
from the card notes. Include a positive case, a refusal/failure case and a recovery
or persistence case. All mutating features inherit RG-01/02/04/07/11; media outputs
also inherit RG-05/06/12; UI surfaces inherit RG-10. Tests below supplement those
shared cases. Choose unresolved semantics in a repository spec before writing a
test that silently makes the product decision. Do not mark a research gate passed
because a helper exists.

## 1.0.8.0 — stabilisation

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-176-pre-release-sweep` — release/docs reconciliation | Map each fixed finding to a failing-before/passing-after reproducer. Reconcile release number, package contents, manifest ABI/checksum and user-facing changelog against one built artifact. Fresh full run records failures/skips honestly; intentionally broken harness check must prevent a green release result. Documentation-only reconciliation must not mark runtime cases passed. |
| `i-108-cross-platform-verification` — OS/host matrix | Windows and Ubuntu Release unit jobs; install and upgrade the same package on both supported Jellyfin host lines. Scan/fix/restore a generated fixture; exercise case/drive/UNC and real cross-volume behavior. Interrupt a swap and restart. Missing runtime/native dependency or host load failure fails the job and retains logs. Track macOS/Docker/manual gaps explicitly. |

## 1.1.0.0 — control and safe repair

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `2d2e24da-197a-4485-9045-68ce3152da85` — per-library policies | Two libraries with opposing policies plus overlapping roots: prove documented precedence and displayed effective policy match execution. Change policy after queueing; stale work uses the new policy. Old configs migrate to equivalent defaults; save/restart preserves overrides without changing other libraries. Preview and fixer resolve the same policy. |
| `i-110-protected-media` — Never modify | Protect title, folder and collection, then scan/queue each applicable fixer: no media mutation. Protection added after approval is rechecked. Refresh/move/item-ID change and restart preserve the documented identity rule. Removing protection deliberately permits work again; Ignore alone must not create protection. Cover manual and combined-fix entry points. |
| `i-bl-12-rung4a-audio-reencode` — audio-only repair | Damaged audio plus healthy video reaches audio repair before full re-encode; prove video packet preservation and clean audio decode. Failed audio repair leaves a recoverable source and escalates only under the selected policy. Multiple tracks, unknown language and last-audio guard preserve required content; history identifies the successful rung. |
| `i-bl-13-repair-only-row-action` — Repair versus Remove | Same irreparable fixture: Repair never deletes/recycles it merely because repair failed; Remove requires the separate authorised action and observes disposal policy. Reject forged/stale action values; dry-run leaves hashes unchanged. Verify button copy, restart-persisted action intent and history distinguish the operations. |
| `i-bl-14-repair-preview` — common preview | For every fixer, preview proposed paths, streams, bytes/space and disposal, then compare execution against it. Preview itself changes no file/queue state. Change source, policy, protection or destination after preview; refuse/recompute rather than execute stale consent. Missing probe/provider produces an honest unavailable preview, never fabricated certainty. Define the shared preview contract first. |
| `i-bl-15-salvage-report` — before/after history | Fixture loses only explicitly authorised damaged content: report exact stream/duration/size changes and retained recovery path. Failure, cancellation and combined passes produce consistent nonduplicated history. Upgrade/reopen DB and paginate/export history without losing technical details or reporting estimates as measured savings. |
| `i-110-minimum-savings` — encoding threshold | Output savings just below, exactly at and above byte/percentage thresholds, including both thresholds configured and zero/invalid values. Below-threshold output is discarded and source hash retained; no retry loop. Larger output still rejected. Repair/compatibility work uses its own acceptance rules. Pin percentage rounding and combined-threshold semantics in the spec. |

## 1.2.0.0 — reliable automation

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-120-retry-policy` — bounded retry/Needs attention | Fake clock and persistent DB: transient failures back off to a ceiling; permanent failures stop; restart preserves attempts/deadline. Scheduling, playback, disabled mode and quota deferrals follow documented counting rules. Retry now after cause resolution succeeds. Two workers cannot consume one attempt twice; one blocked file does not starve healthy work. Test migration/API/UI for the new state. |
| `i-bl-16-plugin-data-growth-controls` — retention/cache/DB space | Advance clock around each retention/TTL boundary with live queue, history and cache rows. Unavailable mount must not be treated as deleted media. Dry-run maintenance reports only; real cleanup preserves protected/live data. Low DB-space and interrupted/locked VACUUM leave a reopenable DB. Upgrade populated historical schemas and validate retention after restart. |
| `i-bl-11-space-cliff-warning` — volume warnings | Source/output/plugin DB on same and different volumes; exact threshold and unknown/unmounted volume. Warn without declaring unknown space safe or healthy, deduplicate recurring alerts and recover after space returns. Save/restart preserves settings; disabling warnings does not bypass the destructive-operation space guard. |
| `3f821f32-bb56-4708-9ba9-fbb7e6906c35` — progress/wait reasons | Zero, one and combined tasks; queue changes mid-run; quota/lock/playback/space wait; cancel and failure. Progress never exceeds its documented denominator and waiting is not presented as failure. Reconnect/restart cannot show a stale running task forever. API and UI agree on totals and final outcome. |
| `i-bl-17-expanded-error-reporting` — diagnostic bundles | Default opt-out and explicit opt-in across upgrade/restart. Seed secrets/paths into nested tags, stderr and command arguments; verify capture/export preview redaction at every nesting level. Enforce time/size/count caps, valid truncated representation and deletion lifecycle. Failed/timed-out capture must not lose the original diagnostic or block repair. Sharing is user initiated; metadata cannot be claimed to reproduce arbitrary corruption. |
| `i-120-digest-alerts` — summaries/alerts | Opt-out causes no delivery; opt-in summary counts unique completed work correctly across retries/combined fixes. Clock/timezone and daily/weekly boundaries do not duplicate delivery after restart. Provider outage/retry cannot block fixes or disclose unredacted paths; disabling stops pending deliveries. Use a fake destination, never real recipients in automated tests. |

## 1.3.0.0 — audio and subtitles

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-180-02` — audio conversion | Bring memory-only design into repository first. Fixture matrix covers immersive/channel/bitrate/sample-rate triggers and every preset, with negative controls that should not convert. Retain-original option adds the compatible track and preserves original packet/metadata identity; removal occurs only with explicit policy. Combined pass remains one encode; incompatible muxer, cancellation and insufficient space preserve source. |
| `bb59c42f-6e70-4d9e-8ef2-7d49fda0bbda` — track retention policy | Original language, commentary, audio description, forced/SDH, duplicate/unknown tags, multiple preferred languages and a single audio stream. Define a table of expected kept/removed indexes for each policy and compare preview, scanner and actual output. Ambiguous metadata preserves content or requests a decision; restart and old-config migration retain defaults. |
| `i-180-05` — subtitle extraction | Text and bitmap formats, forced/SDH language naming, font/style dependencies and multiple streams. Destination file/directory collision and failure after one extraction must not cause partial stripping of embedded subtitles. Stage/verify all required sidecars before media replacement; cancellation/restart remains recoverable. Compare subtitles and timings and confirm Jellyfin discovers/selects the sidecar. |

## 1.4.0.0 — organiser and duplicate automation

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-180-03` — media organiser | Doctor Who 1963/2005/2024 and Silo (2023) fixtures reuse reboot naming semantics; preserve external IDs and timestamps. Move video plus subtitles/art/NFO with target collisions, genuine cross-volume copy and interruption. Preview matches destinations and protected titles never move. Verify watch state, item identity and Recently Added on both hosts; document host limitations before enabling automatic moves. |
| `d58754bb-ad8b-4e49-b99f-11869b6ae6fb` — duplicate auto-fix | Exact duplicate positive control versus different edition/cut, different language/HDR, different content with same name and unavailable winning copy. Only the defined confidence threshold can auto-fix, subject to protection and fix-time winner revalidation. Recycle/restore hash checks prove the losing file remains recoverable; library overlap cannot double-delete. Include threshold boundary and a protected best copy. |

## 1.5.0.0 and 1.6.0.0

| Card ID / feature | Required acceptance cases and evidence |
|---|---|
| `i-180-01` — Library/Overview redesign | Empty, one/many libraries, overlapping selections, stale maintenance data and unavailable drive. API totals match filtered underlying rows; cards show effective policy and blocked work. RG-10 includes all drill-downs, slow/error responses and large counts. Do not resurrect tests that require the previously reverted fields to remain absent once the new contract intentionally adds them. |
| `d04791b1-2625-4da9-bba7-dfa816ae71f5` — Leaving soon/Keep | Fake time at countdown boundaries; new watch activity, multiple users and series grouping. Keep persists across restart/rescan and cancels the review countdown under documented identity rules. Expiry remains review-only and does not delete media. Include deleted user, missing activity data and unavailable library; unknown activity must not be treated as confirmed unwatched. |

## Backlog research gates

| Card ID / feature | Required evidence before scheduling implementation |
|---|---|
| `i-bl-02` — legacy hardware decode | On each proposed GPU/backend, fixture for mpeg2/vc1 and an unsupported profile: demonstrate decode/scale/encode support, output integrity and bounded fallback. Compare throughput to software; mock argument tests alone cannot establish hardware support. Record driver/ffmpeg/device versions. |
| `i-bl-03` — AMF/VideoToolbox decode | Demonstrate GPU-resident scaling on actual supported hardware and current bundled ffmpeg. Unsupported filter/device fails safely to the documented fallback. Measure throughput and decode integrity; no whitelist expansion based only on backend name. |
| `i-bl-05` — configurable repair encoder | Define quality/HDR intent and allowed codecs first. Unsupported/invalid setting, unavailable hardware and old config select a safe documented outcome. Real fixtures verify stream retention, space, output validation and failure recovery for each offered encoder. |
| `i-bl-10-monthly-defrag` — drive maintenance | Product/OS-support decision first. Identify SSD/network/unknown drives and prove no unsupported maintenance launches. Explicit consent, least required permissions, cancellation and recovery must be specified. Research stays blocked without a safe platform-specific test environment; do not run experiments on user media drives. |
| `i-future-stale-auto-delete` — automatic stale deletion | Requires explicit change to PLAN.md's detect-only scope plus the Leaving soon feature. Define all-user veto/Keep, activity uncertainty, grouping and retention. Test final activity change immediately before execution, offline mounts and restore. Countdown expiry alone is never sufficient evidence to delete. |

## Superseded cards

| Card ID | Canonical coverage |
|---|---|
| `i-bl-06` | Audio-only repair cases belong to `i-bl-12-rung4a-audio-reencode`; keep this card superseded, not implemented. |
| `i-bl-09` | Library breakdown cases belong to `i-180-01`; keep this card superseded, not implemented. |

## Keeping this matrix complete

When adding or moving cards, update this file and the card's evidence links in the
same change. A planned row is not a pass. The following read-only check catches
unfinished cards with no matrix row, duplicate rows, and stale rows:

```powershell
$board = Get-Content tools/roadmap/roadmap.json -Raw | ConvertFrom-Json
$matrix = Get-Content docs/testing/ROADMAP-TEST-MATRIX.md -Raw
$rowIds = @([regex]::Matches($matrix, '(?m)^\| `([^`]+)`') |
    ForEach-Object { $_.Groups[1].Value })
$openIds = @($board.items | Where-Object { -not $_.completedAt } |
    ForEach-Object { $_.id })
$missing = @($openIds | Where-Object { $_ -notin $rowIds })
$stale = @($rowIds | Where-Object { $_ -notin $openIds })
$duplicates = @($rowIds | Group-Object | Where-Object Count -gt 1)
if ($missing.Count -or $stale.Count -or $duplicates.Count) {
    throw "Matrix mismatch: missing=$missing stale=$stale duplicate=$($duplicates.Name)"
}
"Covered $($openIds.Count) unfinished roadmap cards (specifications only)."
```
