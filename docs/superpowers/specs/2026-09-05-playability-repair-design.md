# Playability Repair — Design

**Status:** shipped in 1.0.8.0 (unreleased at time of writing)
**Target release:** 1.0.8.0 (feature bundle — see §7)
**Related:** `PlayabilityScanner`, `PlayabilityFixer`, `RecycleBin`, `FfprobeService`, `OutputVerifier`, `SwapRecoveryHelper`

---

## 1. Problem

`PlayabilityScanner` correctly identifies broken files, but `PlayabilityFixer` only deletes them. Many "unplayable" files are container-level damage (bad index, wrong duration, missing moov atom, truncated download, extension mismatch, one corrupt subtitle stream) and are recoverable with a remux or a targeted stream drop. Today's fixer throws all of them away.

## 2. Goal

Add a repair pass to `PlayabilityFixer` that runs *before* the existing delete path. Four repair rungs, each attempted only if the previous one's output failed ffprobe verification. Original file untouched until a rung produces a verified working replacement. If every rung fails, fall through to today's delete-to-recycle-bin behavior.

## 3. Non-goals (v1)

- New scanner or new `IssueType`. Reuse `IssueType.Playability`.
- New fixer class. All logic lives in `PlayabilityFixer`.
- Per-file repair heuristics chosen at scan time. The fixer tries rungs in fixed order.
- Repairing files outside library paths (`LibraryGuard` already blocks this — unchanged).

**~~Audio re-encoding~~** was originally a non-goal (deferred to the audio-conversion scanner) but landed in this ship as rung 4a — see §4.1. The rung 4a use case (single broken audio track, video fine) is narrower than the audio-conversion scanner's remit (codec cleanup across the library) so both fixers can coexist without overlap.

## 4. Design

### 4.1 Repair ladder

Rungs run in fixed order. Each rung's execution is gated by its own config toggle (see §4.4). Between rungs, the previous temp file is discarded.

| # | Name | ffmpeg strategy | Recovers |
|---|---|---|---|
| 1 | **Quick remux** | `-err_detect ignore_err -fflags +genpts+igndts -i <src> -map 0 -c copy -avoid_negative_ts make_zero -y <tmp>` — same extension as source | Bad container index, wrong duration, moov-atom issues, EOF truncation |
| 2 | **Drop broken streams** | Per-stream decode check via ffprobe; identify streams that error; remux with `-map -0:<idx>` excluding them. Same extension. | Files that play but have one corrupt subtitle/audio track breaking decode |
| 3 | **Container coercion** | Remux to `.mkv` (universal container). File is renamed, so **Jellyfin re-indexes it and watch history for the title resets.** | Codec-container mismatch (e.g. HEVC in AVI); files whose extension lies about their container |
| 4a | **Audio-only re-encode** | `-c:v copy -c:a aac -b:a 192k` — keeps source container and file extension. Fires only when the file's single audio track is broken and video decodes cleanly (rung 2 refused because dropping the only audio would leave zero audio streams; without 4a, rung 4 was the only remaining option). Video is copied so this completes in minutes instead of hours; extension is preserved so **watch history survives**. | Files with exactly one audio track that decodes badly, everything else fine |
| 4 | **Re-encode video** | `-c:v libx264 -preset medium -crf 20 -c:a aac -b:a 192k` into MKV. Slow (hours per file). Runs in the background scheduled scan; runtime is not a concern. | Everything else — bitstream damage that survives remuxing |

Each rung writes to a sibling temp file via `TranscodeFixer.SidecarPath` (details in §4.2). `ponytail:` comment on each rung method names the specific failure mode it targets, so future edits know why the rung exists.

### 4.2 Between-rung verification

After each rung writes its temp file, verification runs via `OutputVerifier.VerifyAsync(originalProbe, originalPath, outputPath, ct)` — the same helper used by `TranscodeFixer` and cover-strip. It performs:

- ffprobe the temp.
- Duration within 2s of the original's expected duration (per CLAUDE.md safety invariant #3).
- At least one video stream present (invariant #2).
- At least one audio stream present (invariant #2).
- Full-file decode sample (reuse existing `ProbingScannerBase` sampling — 5s from start, middle, end; for files <60s use `PlayabilityScanner.ShouldSampleWholeFile` full-decode path).

Verify pass → `SwapRecoveryHelper.TryPromoteBeforeDispose` renames the verified temp to `<name>.mediadash-recover-<stamp>.<ext>` (outside every sweep pattern), original is moved to `RecycleBin`, recovery file is finally renamed into place, return `FixResult.Success` with the rung name in the message. Verify fail → delete temp, proceed to next enabled rung.

Temp path naming uses `TranscodeFixer.SidecarPath(path, marker, ext)` — every sidecar ends with a `.mediadash.tmp` non-media tail so Jellyfin's real-time monitor doesn't index it as new media mid-repair.

The promote-before-dispose sequence **is** the "copy before touching" safety requirement — the original is never mutated in place, and a process kill between "temp verified" and "final rename" leaves the salvage at a recover-tagged path that `LibraryGuard.SweepOrphanSidecars` explicitly skips.

### 4.3 Safety hard-stops

- Rung 2 refuses to run if dropping the identified broken streams would leave zero video or zero audio streams. Skip to rung 3 in that case.
- Pre-flight: ≥2× source size free disk before rungs 1–3; ≥3× source size free before rung 4. Fail closed to today's delete path if pre-flight fails.
- Re-verify at fix time that the file is still broken (existing `PlayabilityFixer.IsStillBrokenAsync` — unchanged).
- All destructive steps continue to respect `Configuration.GetDisposal(IssueType.Playability)` and the global dry-run toggle (invariant #4).

### 4.4 Settings card

New card in `configPage.html` under the existing Playability section:

**Title:** Repair broken files
**Blurb:** "Before deleting an unplayable file, MediaDash can try to repair it. Each step is more aggressive than the last. Steps run in order and stop at the first one that produces a working file."

| Checkbox label | Config property (bool) | Default |
|---|---|---|
| Quick remux — fix container damage without re-encoding | `RepairAttemptRemux` | true |
| Drop broken streams — remove tracks that fail to decode (keeps at least one video and one audio track) | `RepairAttemptDropStreams` | true |
| Change container — repack into MKV. **Jellyfin will treat the file as new and watch history for this title will reset.** | `RepairAttemptContainerCoerce` | true |
| Re-encode audio only — repair a single broken audio track without re-encoding video. Keeps source container so watch history is preserved. | `RepairAttemptAudioReencode` | true |
| Re-encode video — last resort. Can take hours per file, runs in the background. | `RepairAttemptReencode` | true |

All five unchecked = repair disabled = identical to today's behavior. No separate master toggle; "all off" IS master-off.

### 4.5 Flow

```
FixAsync(issue)
├── file missing? → fail (unchanged)
├── outside library? → fail (unchanged)
├── IsStillBrokenAsync? no → fail "plays fine now" (unchanged)
│
├── if RepairAttemptRemux           → try rung 1  → verify → swap + return Success
├── if RepairAttemptDropStreams     → try rung 2  → verify → swap + return Success
├── if RepairAttemptContainerCoerce → try rung 3  → verify → swap + return Success
├── if RepairAttemptAudioReencode   → try rung 4a → verify → swap + return Success
├── if RepairAttemptReencode        → try rung 4  → verify → swap + return Success
│
└── fall through → existing delete-to-recycle-bin path (unchanged)
```

Swap = move original to `RecycleBin`; rename verified temp into place; refresh Jellyfin library monitor.

### 4.6 Fix-result messaging

Success messages name the rung so users can see what worked:

- Rung 1: `"repaired <name> (quick remux)"`
- Rung 2: `"repaired <name> (dropped N broken stream(s): <kinds>)"`
- Rung 3: `"repaired <name> (container changed .<old>→.mkv; Jellyfin watch history for this item was reset)"`
- Rung 4a: `"repaired <name> (audio re-encoded, video and container preserved)"`
- Rung 4: `"repaired <name> (video re-encoded)"`
- All rungs failed → today's delete message (unchanged).

## 5. Files touched

Six files (one new, five modified):

1. `Jellyfin.Plugin.MediaDash/Fixers/PlayabilityFixer.cs` — add `TryRepairAsync`, five `TryRung<N>Async` private methods (rungs 1, 2, 3, 4a, 4), verify+swap helpers. Branch inserted before the existing delete block.
2. `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` — five new bool properties (`RepairAttemptRemux`, `RepairAttemptDropStreams`, `RepairAttemptContainerCoerce`, `RepairAttemptAudioReencode`, `RepairAttemptReencode`), all defaults `true`.
3. `Jellyfin.Plugin.MediaDash/Configuration/configPage.html` + `Configuration/scripts/tabs/settings-tab.js` — new "Repair broken files" card with five checkboxes.
4. `Jellyfin.Plugin.MediaDash/Api/RepairSummary.cs` — **new file**. Overview-panel payload: 30-day + lifetime repair counts, bytes rescued, per-rung breakdown for the "Broken files repaired" panel. Serves the §8 follow-up on statistics (partial — endpoint + DTO shipped; Overview UI to follow).
5. `Jellyfin.Plugin.MediaDash.Tests/PlayabilityFixerRepairTests.cs` — new file, unit tests (see §6).
6. `CHANGELOG.md` — bullets per repo format rule.

Real-world test fixtures live in `tools/repair-test/` (new folder — this is test infra, not plugin code).

## 6. Tests

### 6.1 Unit (8)

1. Rung 1 succeeds → temp swap, original in recycle bin, verify called once.
2. Rung 1 fails verify, rung 2 drops one broken subtitle, verifies, swaps.
3. Rung 2 refuses (dropping would leave 0 audio), rung 3 coerces to MKV, verifies, swaps. Success message includes watch-history warning.
4. Rungs 1–3 fail, rung 4 re-encodes, verifies, swaps.
5. All rungs fail → falls through to existing delete path; original recycled, no leftover temp files.
6. Safety: rung 2 never leaves 0 audio or 0 video (asserted directly on the stream-drop planner).
7. Individual toggle OFF skips its rung and moves to the next (parameterized across all four).
8. **Regression:** all four toggles OFF → identical output and side-effects to today's `PlayabilityFixer`.

### 6.2 Real-world (per repo E2E rule)

`tools/repair-test/` contains ten fixtures covering every rung plus a healthy negative control, driven by two validate scripts that make the pass/fail machine-checkable:

| Fixture | Target rung | Broken in |
|---|---|---|
| `rung1-bad-index.mkv` | 1 | MKV cues section chopped |
| `rung1-truncated-mp4.mp4` | 1 | MP4 tail truncated (moov present via faststart) |
| `rung1-noaudio-header.mkv` | 1 | Small tail loss forcing `+genpts+igndts` rewrite |
| `rung2-broken-sub.mkv` | 2 | Junk-bytes SRT alongside good video/audio |
| `rung2-broken-second-audio.mkv` | 2 | Two audio tracks, tail packets XORed |
| `rung3-hevc-in-avi.avi` | 3 | HEVC muxed into AVI (→ `.mkv`) |
| `rung3-flv-with-junk.flv` | 3 | H264-in-FLV, tail scrambled (→ `.mkv`) |
| `rung4-bitstream-damage.mkv` | 4 | 5% mid-file XOR (survives remux) |
| `rung4-heavy-noise.mkv` | 4 | 20% mid-file XOR (rung 3 verify fails) |
| `neg-healthy.mkv` | 0 (must NOT be flagged) | nothing |

Harness (`tools/repair-test/`):

- `regenerate.ps1` — deterministically rebuilds every fixture from `torture/bbb-source.mp4`.
- `validate-fixtures.ps1` — pre-flight; confirms each fixture is broken (or healthy) as declared in `fixture-manifest.json`. Exit 0 = all match.
- `validate-outputs.ps1` — post-flight; confirms the repair produced the right output per rung. Exit 0 = every fixture repaired correctly.
- `validator-self-tests.ps1` — meta-tests the two validators.
- `torture/` + `torture-test.ps1` + `torture-malformed-rows.ps1` — 15+ additional random-corruption variants for stress runs.

Workflow: `regenerate.ps1` → `validate-fixtures.ps1` → point a Jellyfin library at `.\fixtures\`, run MediaDash Scan + Fix → `validate-outputs.ps1`. Plus a manual playback-through-Jellyfin-web-player pass on the fixed fixtures — "the code returned success" is not enough; the file must actually play.

## 7. Version & release

**Target:** 1.0.8.0.

Ships as part of the post-1.0.7.x feature bundle alongside the audio-conversion scanner, media organiser, and Library-tab redesign (per `project_mediadash_roadmap`). Third-digit bump per the locked `feature.feature.feature.bugfix` rule — this adds capability, not a bugfix.

Ordering within the 1.0.8.0 bundle is the roadmap's call; recommendation is media-organiser → audio-conversion → this repair change → Library redesign (touches least surface area first).

Release process unchanged: `tools/release.ps1 -Version X.Y.Z.W -Changelog "..."` (do not hand-edit manifest checksums, per CLAUDE.md).

## 8. Follow-ups (out of scope, note only)

- ~~Fifth rung: audio re-encode.~~ **Shipped in 1.0.8.0 as rung 4a** (§4.1) — narrower scope than a general audio-conversion pass, so the two fixers coexist.
- Per-library repair settings (mirror the audio-scanner pattern once that lands).
- ~~Repair statistics on the Overview tab: "N files repaired this month, X GB saved from deletion."~~ **Fully shipped** — `Api/RepairSummary.cs` DTO + `MediaDashDb.GetRepairSummary` aggregation over the history table + `#mdRepairSummary` Overview card. Schema v9 (`history.saved_bytes`) persists the pre-existing `FixResult.SavedBytes` field. Byte totals count from v1.0.8.0 onwards.
- Configurable rung-4 encoder target (currently hardcoded h264/aac). Wait for user demand before adding the knob.
