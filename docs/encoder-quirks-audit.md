# Encoder Quirks Audit — MediaDash

Compiled 2026-09-15 by research pass. Covers every place MediaDash reads ffprobe
output (`CodecName`, `CodecTagString`, `CodecType`, `Disposition`, `Language`,
`NbFrames`, `ColorTransfer`, `ColorPrimaries`, `BitRate`, `Duration`) against
publicly documented encoder quirks. Findings are ranked P0 (breaks fixes) / P1
(false-positive issues or silent data loss) / P2 (cosmetic).

Legend for file:line risk points is the trunk of `main` on 2026-09-15.

---

## HandBrake

HandBrake is the largest single source of "downloaded from a friend / re-encoded
by user" files in Jellyfin libraries. It routes MP4 through the `ipod` muxer.

### Quirk 1: PGS/VobSub in `.m4v` with empty `codec_tag_string`
- **What**: HandBrake's MP4/M4V pipeline can passthrough VobSub / PGS tracks it
  cannot legally tag inside an MP4 sample entry. ffprobe reports the stream
  with `codec_name=dvd_subtitle` (or `dvdsub`, or empty) and
  `codec_tag_string=""` / `[0][0][0][0]`.
- **Reproduction**: HandBrake CLI 1.7+ with `--subtitle-burned=none --all-subtitles`
  on a DVD source, output `.m4v`. Repro'd 2026-09 in issue #56 (fadern).
- **MediaDash risk**: Already fixed in `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs:57`
  (whitelist `Mp4CompatibleSubtitleCodecs`). Original blacklist bug caused
  bitmap sub tracks to slip past the incompatibility check and the whole remux
  aborted with "Could not find tag for codec".
- **Guardrail**: Already applied.
- **Priority**: P0 (already fixed).

### Quirk 2: `handbrake` encoder tag on Matroska is `Lavf`, on MP4 is `HandBrake`
- **What**: HandBrake sets the format-level `encoder` tag differently per
  container. Any heuristic on `Format` tags for provenance is fragile.
- **MediaDash risk**: MediaDash does not currently key on encoder identity —
  no risk today.
- **Guardrail**: None needed. Note here so future provenance features don't
  read `encoder` naively.
- **Priority**: P2.

### Quirk 3: HDR metadata drop on `.m4v`
- **What**: HandBrake preserves HDR10 (PQ) side data through `.mkv` but
  strips `mastering_display_metadata` and `max_content_light_level` when
  muxing to `.m4v`/`.mp4` before 1.6.x. `color_primaries=bt2020` and
  `color_transfer=smpte2084` are usually still present.
- **Source**: HandBrake issue #4145; ffmpeg trac ticket 8265.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/QualityScanner.cs:215`
  (`IsHdr` checks only `color_primaries` / `color_transfer`) — HandBrake m4v
  HDR content is still correctly detected and skipped. No risk.
- **Guardrail**: None needed.
- **Priority**: P2.

### Quirk 4: HandBrake chapter markers embedded as text `subtitle` stream (rare)
- **What**: Pre-1.5 HandBrake sometimes emits chapter names as a text stream
  with `codec_name=mov_text` and short durations. Ffprobe reports it as a real
  `codec_type=subtitle` stream, not as `codec_type=data`.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/SubtitleLanguageScanner.cs:42`
  (`CodecType == "subtitle"` filter) could match a chapter-name pseudo-stream.
  In practice these are always tagged `language=und` and `LanguageHelper.IsAllowed`
  treats `und` as allowed, so they're never queued for removal. Safe by
  accident.
- **Guardrail**: None needed today. If a future user reports a HandBrake m4v
  losing chapters after a track fix, add a `disposition.timed_thumbnails` /
  short-duration guard in `ComputeRemovableIndexes`.
- **Priority**: P2.

---

## FFmpeg (direct usage / power-user scripts)

### Quirk 5: `-c copy` without `-avoid_negative_ts` produces negative PTS
- **What**: Blu-ray remuxes to `.m2ts` / `.mkv` via `ffmpeg -c copy` frequently
  contain leading negative PTS. Downstream tools drop those packets → duration
  shorter than the source.
- **Source**: ffmpeg trac ticket 6146; own CHANGELOG entry (issue #39).
- **MediaDash risk**: Handled in `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs:289`
  (`-avoid_negative_ts make_zero` + `-fflags +genpts`).
- **Guardrail**: Already applied. `TranscodeFixer.BuildArgs` does NOT set these
  flags — safe because it always re-encodes video (regenerates PTS), but if
  `-c:v copy` is ever added, must add `-avoid_negative_ts` there too.
- **Priority**: P0 (already fixed).

### Quirk 6: Ffmpeg emits `codec_name` as `subrip` for `.srt` streams but
  `codec_tag_string` is empty in Matroska
- **What**: SubRip streams in MKV never have a codec_tag (Matroska has no
  four-CC concept). `codec_tag_string` is `""` while `codec_name=subrip`.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/PlayabilityScanner.cs:143`
  keys `encv`/`enca` DRM detection on `codec_tag_string` only. Empty
  codec_tag on legitimate streams is common and correctly ignored (case-
  insensitive equality of "" with "encv" is false).
- **Guardrail**: None needed.
- **Priority**: P2.

---

## MakeMKV (Blu-ray / DVD rippers)

### Quirk 7: Container duration derived from a non-video stream
- **What**: MakeMKV writes Format.Duration from the longest stream. On Blu-ray
  discs with a long trailing PCM audio track (or seamless-branching main-
  movie playlists), that's longer than the video. Removing that audio via
  `TrackFixer` makes the new file's Format.Duration shorter than the source's
  → naïve duration-only verification fails.
- **Source**: Own CHANGELOG entry ("Spider-Verse AV1, Lion King 2019 and
  similar"), issue #39.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Fixers/OutputVerifier.cs:88`
  three-layer ladder handles this (video-stream Duration → nb_frames →
  packet-count rescue).
- **Guardrail**: Already applied.
- **Priority**: P0 (already fixed).

### Quirk 8: PGS with `disposition.forced=1` and no language
- **What**: MakeMKV copies the Blu-ray "forced subtitle" disposition bit
  verbatim, but many discs never set language on the PGS track — ffprobe
  reports `language=und` (or missing).
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/SubtitleLanguageScanner.cs:42`
  reads `s.Language` via `LanguageHelper.IsAllowed`, which treats `und` as
  allowed — the forced PGS stream stays. Correct behaviour.
- **Guardrail**: None needed. Note: `disposition.forced` is NOT currently
  read by any scanner; if we ever want to preserve forced tracks explicitly
  regardless of language allow-list, plug it into
  `SubtitleLanguageScanner.EvaluateAsync` next to the `IsHearingImpaired`
  guard on line 44.
- **Priority**: P2.

### Quirk 9: MakeMKV writes MKV `STATISTICS_TAGS` and `DURATION` per-stream
- **What**: Each stream carries `tags.DURATION` in `HH:MM:SS.nnnnnnnnn` and
  `tags.NUMBER_OF_FRAMES` — sometimes with language suffixes
  (`DURATION-eng`).
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Fixers/OutputVerifier.cs:200`
  already reads `DURATION` tags with a `StartsWith("DURATION")` prefix match.
- **Guardrail**: Already applied. Note: `NUMBER_OF_FRAMES` (per-stream, MKV-
  specific) is not read — `NbFrames` from the top-level `nb_frames` field is
  used instead, which mkvmerge does populate. Safe.
- **Priority**: P2.

---

## MKVToolNix / mkvmerge (post-processing)

### Quirk 10: `mkvmerge --disable-track-statistics-tags` removes `nb_frames`
- **What**: If a user re-muxes with `mkvmerge --disable-track-statistics-tags`,
  the resulting MKV has no `NUMBER_OF_FRAMES` and ffprobe reports
  `nb_frames=""`.
- **Source**: mkvmerge docs, `--disable-track-statistics-tags` flag.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Fixers/OutputVerifier.cs:100`
  falls through Layer 2 (nb_frames unavailable) to Layer 3 (packet-count
  walk). Slow but correct.
- **Guardrail**: Already applied.
- **Priority**: P2.

### Quirk 11: mkvmerge splits multi-audio Atmos into TrueHD + AC3 fallback
- **What**: Some tools mux Dolby Atmos as TrueHD with an embedded AC3 core.
  ffprobe reports codec_name=truehd (main) + codec_name=ac3 (secondary)
  BOTH with the same `language=eng` and both with `disposition.default=1`
  set by different pipelines inconsistently.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/AudioLanguageScanner.cs:52`
  keeps ALL matching-language tracks by default (removes only unwanted-lang
  tracks). Two eng-tagged tracks are both kept — correct.
  BUT: `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs:534` — `keptAudio`
  logic keeps all when a language filter matches multiple streams. Safe.
- **Guardrail**: None needed. Multi-track-same-language is not assumed unique
  anywhere.
- **Priority**: P2.

---

## Radarr / Sonarr with post-processing scripts

### Quirk 12: Radarr custom-script transcodes to `.mkv` with empty
  `codec_tag_string` on every stream
- **What**: Radarr recyclarr / TRaSH-guide FFmpeg recipes often pipe through
  a Matroska container. All streams end up with empty `codec_tag_string`.
- **MediaDash risk**: See Quirk 6 — MediaDash's `encv`/`enca` DRM detection
  is safe against empty tags.
- **Guardrail**: None needed.
- **Priority**: P2.

### Quirk 13: Sonarr HDR10+ / DoVi Profile 7 sidecar streams
- **What**: DoVi Profile 7 rips carry a hidden Enhancement Layer as a second
  video stream with `disposition.attached_pic=0` but very small dimensions
  and `codec_name=hevc`. Called "EL" or "Dolby Vision RPU".
- **Source**: DoVi tool ecosystem docs; Reddit r/jellyfin recurring thread.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Fixers/OutputVerifier.cs:61`
  requires "the new file has A video stream" (Any). A DoVi remux losing the
  EL still has the main video and passes verification, but downstream
  playback loses HDR. The user's file plays; the DoVi profile silently
  downgrades.
  Also, `Jellyfin.Plugin.MediaDash/Scanners/QualityScanner.cs:89`
  `FirstOrDefault(video)` picks stream 0 which is usually the base layer —
  correct.
- **Guardrail**: For `TrackFixer` this is fine (video streams are never
  removed there). For `TranscodeFixer`, add a scanner-level skip when the
  file has a second `codec_type=video` stream with `codec_name=hevc` — that
  file is DoVi P7 and any transcode without the DoVi CLI destroys the
  profile.
  Suggested one-liner in `QualityScanner.EvaluateAsync` after `IsHdr` guard:
  ```csharp
  // Dolby Vision Profile 7 uses two hevc video streams (base + EL). Any
  // naive transcode drops the EL and silently downgrades. Skip until we
  // add DoVi-aware encoding.
  if (probe.Streams!.Count(s => string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)
      && string.Equals(s.CodecName, "hevc", StringComparison.OrdinalIgnoreCase)) >= 2)
  {
      return Task.FromResult<Issue?>(null);
  }
  ```
- **Priority**: P1.

---

## Plex Media Server transcoded outputs

Plex writes optimized-versions with encoder tag `PlexTranscoder` and
`format_name=mp4`. Nothing unusual for MediaDash — no findings.

- **Priority**: P2 (no risk).

---

## yt-dlp / streamlink

### Quirk 14: yt-dlp embedded thumbnails muxed as `codec_type=video` MJPEG
- **What**: `yt-dlp --embed-thumbnail` inside an `.mkv` produces a second
  video stream with `codec_name=mjpeg` and `disposition.attached_pic=1`.
- **Source**: yt-dlp docs, `--embed-thumbnail` flag; ffmpeg-user list.
- **MediaDash risk**: Three risk points.
  1. `Jellyfin.Plugin.MediaDash/Scanners/EmbeddedCoverArtScanner.cs:262`
     — `HasEmbeddedCover` matches MJPEG on `codec_type=video`. But scanner
     is gated to audio libraries (`IsAudioLibrary` check on line 224), so an
     MKV in a Movie library is never scanned. Safe.
  2. `Jellyfin.Plugin.MediaDash/Fixers/OutputVerifier.cs:61` — "any video
     stream" check accepts the file. Correct.
  3. `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs:531` — maps
     `0:{video.Index}` where `video` is the FIRST video stream. In a
     `yt-dlp` MKV the attached_pic MJPEG can be stream 0 (title/upload
     screenshot inserted first). The transcoder would then re-encode the
     thumbnail as the "movie".
- **Guardrail (P1)**: In `Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs:112`
  and `Jellyfin.Plugin.MediaDash/Scanners/QualityScanner.cs:89`, change:
  ```csharp
  var video = probe?.Streams?.FirstOrDefault(s =>
      string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase));
  ```
  to skip attached-picture streams:
  ```csharp
  var video = probe?.Streams?.FirstOrDefault(s =>
      string.Equals(s.CodecType, "video", StringComparison.OrdinalIgnoreCase)
      && (s.Disposition is null || !s.Disposition.TryGetValue("attached_pic", out var ap) || ap == 0));
  ```
  Requires exposing `Disposition.TryGetValue("attached_pic")` — already
  usable via the existing `Disposition` dictionary; no new field on
  `FfprobeStreamInfo`.
- **Priority**: P1.

### Quirk 15: yt-dlp chapter metadata as MKV `chapters` — NOT streams
- **What**: yt-dlp `--embed-chapters` writes Matroska `chapters`, not a
  `codec_type=data` stream. ffprobe with `-show_chapters` surfaces them; we
  don't request that, so they're invisible.
- **MediaDash risk**: None. TranscodeFixer preserves via `-map_chapters 0`
  (`Jellyfin.Plugin.MediaDash/Fixers/TranscodeFixer.cs:689`). TrackFixer's
  `-map 0` also brings chapters. Safe.
- **Priority**: P2.

---

## DaVinci Resolve exports

### Quirk 16: Timecode track as `codec_type=data`
- **What**: Resolve MOV/MP4 exports contain a timecode track ffprobe reports
  as `codec_type=data` codec_tag=`tmcd`.
- **MediaDash risk**: No scanner filters on `codec_type=data`. `TrackFixer`
  and `TranscodeFixer` both use `-map 0` which brings the tmcd track — some
  muxer / codec combinations reject it and abort with "Could not find tag".
- **Guardrail (P1)**: Add negative-map for data streams in TrackFixer when
  target extension is MP4-family. In
  `Jellyfin.Plugin.MediaDash/Fixers/TrackFixer.cs:282`, next to the existing
  `-map -0:N` per-stream negatives, emit `-map -0:d` when
  `Mp4FamilyExtensions.Contains(ext)`. One line.
- **Priority**: P1.

---

## Commercial rippers (DVDFab, AnyDVD, VidCoder)

### Quirk 17: VidCoder (HandBrake GUI wrapper) — same quirks as HandBrake
See Quirks 1–4. VidCoder passes options straight through.

### Quirk 18: DVDFab occasionally leaves `encrypted=1` metadata even after
  DRM strip
- **What**: DVDFab's "remove copy protection" flow can leave `encrypted` /
  `drm` metadata tags on the format while the actual sample entries are
  plaintext `mp4a`/`avc1`.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/PlayabilityScanner.cs:143`
  keys on `codec_tag_string=encv/enca` (per-stream), NOT format tags. Safe.
- **Priority**: P2.

---

## StaxRip / MeGUI (Windows power-user tools)

### Quirk 19: MeGUI x264 output with `stereo` disposition bits set on audio
- **What**: MeGUI's AviSynth pipeline occasionally sets `disposition.dub=1`
  on the primary English audio track for content that came in as a foreign
  dub.
- **MediaDash risk**: No scanner reads `disposition.dub`. Safe.
- **Priority**: P2.

---

## Audio encoders

### Quirk 20: iTunes / Apple Music AAC — VBR reported per-stream but not per-
  container
- **What**: Apple Music `.m4a` sets `stream.BitRate` (variable bitrate
  average) but `Format.BitRate` matches, so no divergence.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/QualityScanner.cs:56`
  reads per-stream bitrate for oversized-audio detection. Correct.
- **Priority**: P2.

### Quirk 21: Apple Music cover art as `codec_type=video` mjpeg with
  `disposition.attached_pic=1`
- **What**: Standard behaviour. Same class as Quirk 14.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Scanners/EmbeddedCoverArtScanner.cs`
  actively LOOKS for this to extract folder covers. Correct.
  `Jellyfin.Plugin.MediaDash/Fixers/EmbeddedCoverArtFixer.cs:307` uses
  `-map 0 -map -0:v` to strip video streams — this removes ALL video
  streams including a legitimate music video attached to an audio file
  (rare, but possible with `.m4a` "music video" files). Ponytail acceptable:
  audio-quality scanner never runs on video containers.
- **Priority**: P2.

### Quirk 22: X Lossless Decoder (XLD) FLAC — chapter tracks stored as CUE
  in `tags.CUESHEET`
- **What**: XLD one-file rips embed the CUE as a top-level FLAC tag, not a
  ffprobe stream. Invisible to stream-based scanners.
- **MediaDash risk**: None. `EmbeddedCoverArtScanner` and `QualityScanner`
  don't read CUE tags.
- **Priority**: P2.

### Quirk 23: dBpoweramp FLAC with embedded APEv2 tags AND Vorbis Comments
- **What**: ffprobe can report inconsistent `language` between the two tag
  systems for the same file.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Probing/FfprobeStreamInfo.cs:102`
  reads `Tags["language"]` (first hit only). Whatever ffprobe merged wins.
  No dedup logic needed.
- **Priority**: P2.

### Quirk 24: Foobar2000 Opus encoder writes `R128_TRACK_GAIN` but sometimes
  omits `language` on multi-channel Opus
- **MediaDash risk**: Missing `language` → `Normalize` returns `"und"` →
  `IsAllowed` returns true → track kept. Safe.
- **Priority**: P2.

### Quirk 25: Whisper / podcast tools — `.opus` with `codec_name=opus` but
  `codec_tag_string=Opus` (capitalised)
- **What**: Some Whisper-derived tooling writes non-canonical codec tags.
- **MediaDash risk**: No case-sensitive `codec_tag_string` checks exist
  today. Only `encv`/`enca` are compared and those use
  `OrdinalIgnoreCase`. Safe.
- **Priority**: P2.

---

## Cross-cutting quirks (encoder-agnostic, but worth calling out)

### Quirk 26: `codec_name` variance in ffmpeg N-6xxxx builds
- **What**: `dvdsub` (old) vs `dvd_subtitle` (new), `pgssub` (old) vs
  `hdmv_pgs_subtitle` (new), `dvb_subtitle` vs `dvbsub`, `xsub` from DivX
  files.
- **MediaDash risk**: Already covered by whitelist in `TrackFixer.cs:57`.
- **Priority**: P0 (already fixed).

### Quirk 27: `nb_frames="N/A"` on MPEG-TS live-recorded and yt-dlp streams
- **What**: MPEG-TS has no container-level frame count. ffprobe returns
  the literal string `"N/A"` for some builds and empty for others.
- **MediaDash risk**: `Jellyfin.Plugin.MediaDash/Fixers/OutputVerifier.cs:222`
  parses `NbFrames` as `long` — "N/A" fails parsing, returns null, falls
  through to packet-count rescue. Correct.
- **Priority**: P2.

### Quirk 28: `Language="und"` from container-level `metadata` file tags on
  single-audio files
- **What**: ffprobe reports the file's metadata-block language as the audio
  stream's language when the stream itself has none. Confusing but not
  broken.
- **MediaDash risk**: `LanguageHelper` treats `und` as allowed. No incorrect
  removals possible.
- **Priority**: P2.

---

## Consolidated action list (P0 / P1 only)

All P0s are already applied — listed here so any future refactor keeps them.

| # | File:line | Change | Priority |
|---|-----------|--------|----------|
| Q1  | `Fixers/TrackFixer.cs:57`     | Whitelist-based subtitle-codec check. **Keep the whitelist**; do not regress to a blacklist. | P0 (applied) |
| Q5  | `Fixers/TrackFixer.cs:289`    | `-avoid_negative_ts make_zero` + `-fflags +genpts` on the remux command. **Keep on every future muxer arg builder.** | P0 (applied) |
| Q7  | `Fixers/OutputVerifier.cs:88` | Three-layer duration → nb_frames → packet-count ladder. **Do not simplify back to duration-only.** | P0 (applied) |
| Q26 | `Fixers/TrackFixer.cs:57`     | Same whitelist covers `dvdsub`/`dvd_subtitle`/`pgssub`/`hdmv_pgs_subtitle`/`dvbsub`/`xsub`/empty. | P0 (applied) |
| Q13 | `Scanners/QualityScanner.cs` after `IsHdr` guard | Skip files with two `codec_type=video codec_name=hevc` streams (Dolby Vision Profile 7). ~5 lines. | P1 |
| Q14 | `Scanners/QualityScanner.cs:89` and `Fixers/TranscodeFixer.cs:112` | Change `FirstOrDefault` video-stream picker to skip streams with `disposition.attached_pic=1`. Prevents transcoder from re-encoding a yt-dlp thumbnail as the "movie". | P1 |
| Q16 | `Fixers/TrackFixer.cs:282` remux args | When target ext is in `Mp4FamilyExtensions`, add `-map -0:d` to drop data streams (Resolve tmcd). One line. | P1 |

No P0 gaps found — the recent whitelist flip and the three-layer verification
ladder together neutralise every publicly known encoder quirk in this class.
The three P1s are the outstanding lazy fixes worth batching into one patch.
