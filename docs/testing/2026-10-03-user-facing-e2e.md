# User-facing common-format E2E — 2026-10-03

## Verdict and tested build

This live acceptance sweep found product defects and coverage gaps. It is not a release sign-off. Findings apply to the DLL installed on the local test server; the current working-tree DLL is different and was not deployed for this request.

- Jellyfin 10.11.11, Windows, `http://localhost:8099`, local `jellyfin-v10` data directory.
- Installed MediaDash reports Active `0.9.0.0`; the dashboard reports `v0.9.0` and offers an update to `1.0.7.7`.
- Installed DLL SHA-256: `E4F5F084CCEDE84C1D2388B80D2BB9D6D464FE1F9C0C10CE2B5FD1E11333DFC8`.
- Existing publish DLL SHA-256: `A005A1A390F9F4AEF4DD0636EA4725E627F71B7815E4E4DB2991784F6873AACD`.
- Production was not contacted. No plugin deployment, server restart, Git reset or publication was performed.

## Library and evidence

The pristine common-format corpus contains **89 fixtures**, registered during testing as five typed Jellyfin libraries. The separate repair matrix contains 15 repair fixtures plus an excluded-library control. Generated media and evidence remain on disk; temporary library registrations are removed during cleanup.

| Group | Formats and variants |
|---|---|
| Video | MP4, MKV, MOV, M4V, AVI, WebM, TS, MPG, WMV; H.264, HEVC, AV1, synthetic 10-bit HDR metadata; silent video and STRM |
| Audio | MP3, FLAC, WAV, OGG/Vorbis, Opus, M4A, AAC, WMA, M4B; English, French-only, multiple language tracks, attached cover art |
| Images | JPEG, PNG, GIF, WebP, BMP, TIFF |
| Books/comics | PDF, EPUB, CBZ, real RAR-backed CBR, real 7-Zip-backed CB7, Calibre-generated MOBI, TXT; EPUB carrying a DRM marker |
| Common faults | Zero bytes and invalid payloads for each of the 18 video/audio extensions; MP4/MKV truncation; wrong extension; duplicate and oversized candidates; broken PDF/EPUB/CBZ, malformed NFO, inert script, orphan subtitle |
| Repair-specific faults | Recoverable container damage, corrupt video, corrupt secondary audio, audio-only repair, heavy truncation, container coercion, rename collision, repair disabled, excluded library |
| Sidecars | English SRT, ASS and WebVTT; NFO; sidecar preservation around repairs |

Primary evidence:

- [Pristine corpus and manifest](C:/dev/mediadash-e2e-20261003-common-library/fixtures.json)
- [Common-format results](C:/dev/mediadash-e2e-20261003-formats-02/results.json)
- [Detected owned issues](C:/dev/mediadash-e2e-20261003-formats-02/issues-before.json)
- [Jellyfin indexed items](C:/dev/mediadash-e2e-20261003-formats-02/items-before.json)
- [Repair results](C:/dev/mediadash-e2e-20261003-repair/results.json)
- [Repair history](C:/dev/mediadash-e2e-20261003-repair/history-all.json)
- [Repeatable runner](C:/dev/mediadash/tools/repair-test/run_formats.py)
- [Run instructions](C:/dev/mediadash/tools/repair-test/README.md)
- [Per-fixture reactions, including actual fix messages](C:/dev/mediadash-e2e-20261003-common-library/reactions.csv)
- [Final hash and queue validation](C:/dev/mediadash-e2e-20261003-common-library/final-validation.json)
- [Focused duplicate, quality, cover and image results](C:/dev/mediadash-e2e-20261003-followup-02/results.json)
- [Browser restore results](C:/dev/mediadash-e2e-20261003-ui-restore/results.json)

## Observed findings

### E2E-01 — P1: a valid TXT book folder is offered for deletion

The Books fixture `plain-text/plain-text.txt` receives a format-conversion proposal, while its containing folder receives OrphanedDebris with the suggested action **Delete media-free folder "plain-text"**. A valid user book is therefore simultaneously treated as convertible content and disposable debris. This folder action was not approved. The current source explains the conflict: `MediaFormats.Books` omits `.txt`, and `OrphanCleanupScanner` uses `MediaFormats.All` to determine whether a folder contains media. This source trace supports the live symptom; it does not prove every safety check in the installed fixer would allow deletion.

### E2E-02 — P1: an excluded Books library receives a merge proposal

While EnabledLibraries contained only the five generated library IDs, the installed scanner proposed merging three audiobook chapters in `C:\dev\mediadash-e2e-books\phase3-audiobook`, outside the owned test library. That proposal was not approved. The working-tree `FormatCompatibilityScanner` already contains explicit EnabledLibraries filtering for the unindexed sweep and audiobook-folder pass; the installed build must be retested after deployment before calling this resolved.

### E2E-03 — P2: corrupt secondary audio is missed

The independent repair fixture has decodable video and first audio but a failing second audio stream. The scanner fails to report Playability. Full stream-specific FFmpeg decode verifies the damage, so successful default-stream playback cannot clear this case.

### E2E-04 — P2: BMP/TIFF conversion is never offered in Photos libraries

Jellyfin indexes the healthy BMP and TIFF fixtures as Photo items, but MediaDash does not report their expected format-compatibility proposals despite image conversion being enabled. Current `ScanTask.IncludeItemTypes` omits Photo; the unindexed-format sweep cannot compensate for already-indexed images. These cases remain detection failures, not fabricated conversion successes.

### E2E-05 — P2: Overview reports nonempty libraries as empty

The Overview library breakdown shows **0 items / 0 B** for Music, Books and Photos even though the indexed-items evidence contains 25 Audio, 10 Book and six Photo items. Audiobooks and Movies are counted. The current `MediaDashController.GetLibraryStats` query includes Movie, Episode, MusicVideo and AudioBook but omits Audio, Book and Photo, matching the live discrepancy.

### E2E-06 — P2: invalid files receive a misleading tool-health error

For several invalid nonempty payloads, Fix refuses safely and preserves the source but reports “ffprobe didn't respond (missing, hung, or errored)” and advises changing FFmpeg settings. The same executable successfully probes valid fixtures. This confuses invalid input with a broken installation. Some containers, notably M4V, take the normal unplayable-file recycle route instead; that variation is not evidence of an unsafe deletion when the original is recoverable.

### E2E-07 — investigation: heavily truncated MP4 is recycled instead of partially repaired

The repair matrix expects partial recovery but the installed plugin exhausts its repair ladder and recycles the original. The original remains recoverable. Four expectation failures cover the method, accounting, disclosure and output of this one scenario; they are not four independent defects. Further work must establish whether usable content can actually be recovered before concluding that the fallback itself is incorrect.

### E2E-08 — P2: detected broken books cannot complete the offered fix

Broken PDF, EPUB and CBZ fixtures are correctly reported, but their approved Playability fixes fail with the same ffprobe tool-health message as invalid video. The originals remain unchanged. The user has actionable-looking findings whose fix path relies on a media probe unsuitable for those document containers. This is a functional refusal, not loss of user content; damaged books require a document-aware refusal/disposal path and an accurate explanation.

### E2E-09 — coverage gap: corrupt Photos are missed

The focused follow-up adds invalid JPEG and PNG payloads to a Photos library. Neither receives Playability detection, so no repair or disposal proposal can be tested. Both originals remain unchanged. This extends the Photo item-type gap in E2E-04: healthy browser-incompatible images and damaged photo content are both outside the observed scanner coverage.

### E2E-10 — P3: restore feedback crowds the row actions

After restoring beside an existing file, the desktop recycle-bin row correctly explains the recovered filename and that both copies are kept. The long success message wraps into a very narrow actions column and overlaps the adjacent Delete control. See the restore screenshot below. The operation itself passes hash and decode checks; this is a presentation issue.

## Harness issues and corrected expectations

- The original `--hold-existing-queue` implementation reverted 14 unrelated approvals to Detected. Scanner invalidation then removed their rows, preventing normal cleanup from restoring them. The authentic snapshots were retained and restored with their original IDs/fields; a SQLite backup was taken before this narrowly scoped recovery. No synthetic fixture issues were inserted. The runner now holds approvals in Dismissed state and reverts/approves them during cleanup. The installed build's unfiltered Issues endpoint returns only Detected rows, so cleanup must explicitly query `status=Dismissed` in addition to open work. The final live hold/rescan/restore check passes for all 14 approvals, including exact API fields.
- A preceding failed junk-file repair remained Queued and contaminated the following header-corruption case in the older repair matrix. That case was blocked by the runner's foreign-queue guard, not a demonstrated header-repair failure.
- The first duplicate candidates had different normalized filenames, so they were treated as separate editions. The focused follow-up uses identical basenames in different folders.
- The first oversized candidate was an MKV without per-stream bitrate and a generous 100 Mbps ceiling. The follow-up uses MP4 bitrate metadata and an explicit lower ceiling. Its initial absence is not counted as a confirmed plugin defect.
- Initial junk-fixture expectations demanded refusal for every container. M4V instead recycled the invalid input safely. The reusable matrix now accepts either refusal with unchanged bytes or successful disposal with an exact recoverable original, and restores recycled originals.
- The combined audio/subtitle action records two issue histories sharing one recycle path. Restoring both independently makes the second call return HTTP 409 because the first already restored the original. The runner now restores each physical recycle path once; the focused follow-up repeats this workflow.
- Duplicate disposal removes its emptied dedicated folder. The first focused harness attempted to enumerate that deleted folder before restoring the file and raised an exception. The runner now accepts an absent output folder. Browser restoration subsequently recovered both the exact duplicate video and its NFO; the better copy's hash stayed unchanged.

## User-facing checks and limits

The browser exercised the dashboard, Issues filtering, approving and undoing removal of unwanted audio, playback of repaired media, filtered conversion history, Errors, and recycle-bin recovery. Approval/undo preserved the filename filter. It restored the deleted duplicate video and its NFO, then restored a recycled track-fixed video beside an existing original. The interface reported `multi-language-restored.mkv`; independent hashes and full decode confirmed the recovered copy while the existing original and duplicate keeper remained unchanged. Desktop screenshots and a 390×844 mobile viewport were captured; the mobile document had no horizontal overflow (380 px content/scroll width). Light and dark appearances were observed during the browser session; all four viewport/theme combinations were not exhaustively exercised.

The Files manual-delete flow opened a native confirmation that stalled browser automation. The owned file remained present; the manual-delete confirmation workflow is therefore unverified, not a confirmed plugin defect. Browser recycle-bin restore passed independently. With background fixing paused and the page reloaded, History, Issues, Errors and Recycle bin navigation/filtering worked; earlier transient navigation attempts are not reported as product failures.

![Owned audio issue approval](C:/dev/mediadash-e2e-20261003-formats-02/ui-approved.png)

![Mobile dashboard](C:/dev/mediadash-e2e-20261003-formats-02/ui-mobile.png)

![Conversion history](C:/dev/mediadash-e2e-20261003-followup-02/ui-history.png)

![Browser restore preserves an existing file](C:/dev/mediadash-e2e-20261003-ui-restore/ui-restored-copy.png)

This sweep does not establish Linux/Docker/v12 behavior, real encrypted-book handling, HDR rendering accuracy, all mobile clients, HEIC/RAW camera compatibility, full-length media stress, permission-denied/ENOSPC/network-volume recovery, crash recovery, hardware encoding, or every organiser/stale-content workflow. Synthetic short fixtures cover common formats and faults rather than every possible file. No common-format assertion count should be read as a count of independent user scenarios.

## Final results and cleanup

| Run | Passing assertions | Failing assertions | Interpretation |
|---|---:|---:|---|
| Repair matrix: 15 fixtures plus excluded control | 256 | 8 | Secondary-audio detection; four partial-recovery expectations; header/rescan contamination; original queue-holding bug |
| Common formats: 89 fixtures | 551 | 16 | Four BMP/TIFF detection/fix gaps; three broken-book fixes; four initial duplicate/quality expectations; four safe junk-recycling expectation mismatches; one repeated shared-bin restore |
| Focused follow-up: seven fixtures | 55 | 5 | Four corrupt JPEG/PNG detection/fix gaps and one harness attempt to enumerate a removed duplicate folder |
| Browser recovery | 17 | 0 | Exact duplicate and NFO recovery; non-overwriting suffixed restore; full decode and attributable history |
| Final queue hold/rescan/restore check | 9 | 0 | All 14 authentic approval IDs and fields survive; original configuration restored |
| Python harness self-tests | 22 | 0 | Includes held-queue restoration order and explicit dismissed-row lookup |

These are raw run results; earlier failed runs are retained rather than rewritten as green. Corrected duplicate and quality detection/fixing, combined track restoration, and cover extraction passed the focused checks. Corruption repair, container coercion, extension correction, full HTTP delivery, dry run, original recycling/restoration, sidecar preservation, excluded-library preservation and failure refusals have direct evidence in the repair/common matrices. Browser playback was observed separately.

Final cleanup is verified: all 89 broad-run originals, all seven focused originals and the pristine corpus match their SHA-256 manifests. Temporary library registrations were removed. No foreign history writes occurred during the fix matrices. All 14 original approvals are Queued under their exact original IDs; the final API-field comparison and configuration restoration passed. The local server started for this task has been shut down, restoring its initial stopped state. The shared recycle bin and history were not emptied; owned replacement copies/evidence remain for investigation.

[Queue recovery evidence](C:/dev/mediadash-e2e-20261003-queue-recovery/recovery.json), [live queue-holding results](C:/dev/mediadash-e2e-20261003-queue-hold-check-02/results.json), [cleanup evidence](C:/dev/mediadash-e2e-20261003-queue-hold-check-02/cleanup.json).
