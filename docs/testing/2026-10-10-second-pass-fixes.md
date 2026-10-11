# Follow-up review fixes — 10 October 2026

All four findings from the second local review were fixed in the existing working tree. Unrelated edits were preserved. No commit, push, deployment or Jellyfin restart was performed.

## Changes

- Seek-audio repair uses the existing container mapping to provide an explicit FFmpeg output format for its temporary filename. Tests cover Matroska, MP4 and MPEG-TS. The production argument builder also succeeded with real FFmpeg on generated Matroska media.
- Combined cleanup checks both categories' modes, disabled lists, protection and queued approvals before starting and before replacement. An unauthorized companion falls back to audio-only cleanup without resolving the subtitle issue. External subtitle disposal also rechecks the disabled list and combined authorization.
- Duplicate sidecar disposal checks protection separately for each file and retains the containing folder when protected files remain. Owned fixtures cover permanent deletion and recycling, preserving the keeper and protected metadata while disposing ordinary artwork. Recycling still reports the removed artwork for restore.
- Interleave verification refuses unknown or invalid duration, nonfinite packet timestamps, missing sampled-video evidence and incomplete full-walk stream evidence. Successful empty ffprobe output and a video-only sample followed by an empty full walk both return Indeterminate. Healthy complete samples retain their existing behavior.

## Validation

- Focused C# tests: **47 passed**, including **21 new regression cases**.
- Full Release C# suite: **1,520 passed, 1 skipped, 0 failed**. The existing source-identity integration placeholder remains skipped.
- Dashboard regression suite: **25 passed**; lifecycle suite: **all assertions passed**.
- Release publish: passed, output `C:\dev\ship-review-20261010\second-fixed-publish`.
- Published DLL SHA-256: `4AD329867156582FACEC13DD6FCBF7B3609BE9A1DB5387E455C50676185D40C1`.
- Targeted tracked-file whitespace check: passed.
- Owned executable probes: protected metadata retained; empty sample and full-walk evidence refused; actual FFmpeg repair command produced a nonempty temporary Matroska output.

Full test result: `Jellyfin.Plugin.MediaDash.Tests/TestResults/ship-second-fixes-20261010.trx`. The isolated executable probe lives outside the repository at `C:\dev\ship-review-20261010\second-pass`.

## Release boundary

**Do-not-push for release yet.** Fresh live Jellyfin detect → approve → fix → rescan/history → playback/seek → restore acceptance against the final DLL, supported-host validation and interleave client/resource checks remain outstanding. This pass did not exercise live browser behavior, Linux/Docker/macOS, GPU execution, crash recovery or disk-full injection.
