# Local Jellyfin acceptance — 10 October 2026

The later [release-blocker follow-up](2026-10-10-release-blocker-fixes.md) supersedes the installed-build identity and several remaining test gates below.

The current local working-tree build was installed on the isolated Windows Jellyfin 10.11.11 host at `localhost:8099`, with explicit user approval. The session's nine review fixes were exercised through live acceptance or the filesystem/process regression checks identified below. Live testing found and fixed two additional problems: secondary-audio revalidation/verification missed damaged nondefault tracks, and partial-repair messages inferred lost footage from an unreliable duration header.

## Installed build and validation

- Final DLL SHA-256: `3B48F6898DBA46A9CC68351D3B6686BAFFB500893B14472A589CF3CD09D68129`.
- Installed directory: `%LOCALAPPDATA%\jellyfin-v10\plugins\MediaDash_0.9.0.0`.
- Host executable: `%USERPROFILE%\Downloads\jellyfin_10.11.11-amd64\jellyfin\jellyfin.exe`; data directory `%LOCALAPPDATA%\jellyfin-v10`.
- The local build reports plugin version `0.0.0.0`; installation identity is established by the DLL hash and Active status, rather than release-version metadata.
- Full Release suite: **1,522 passed, one skipped, zero failed** (`live-final-20261010.trx`). The existing source-identity integration placeholder remains skipped.
- UI regression suite: **25 passed**; UI lifecycle suite: **all assertions passed**.
- Supporting Windows filesystem, policy, interleave and repair regressions: **73 passed**. Harness self-tests: **22 passed**.
- Corrected repair retest: **66 live assertions passed, zero failed**.
- Final complete feature run: **130 live assertions passed, zero failed**, using **23 owned fixtures across 20 scenarios**.
- Final wording-only build acceptance: **17 live assertions passed, zero failed**, including preserved healthy video, corrected message, counters, full decode, restore and cleanup. The three successful acceptance runs total **213 assertions**; preliminary failures are retained below.

The 66-assertion repair retest and 130-assertion feature run used DLL `90E42C8B0D929716288A3CF32ABEA0DAE9EF145B602C62D3AAF54A2840CD1DAB`. The final DLL changes only the partial-repair wording from that tested implementation. The final wording, accounting, healthy-video preservation, decode and restore are checked by an additional focused live run against the final installed DLL.

## Session coverage

| Reviewed feature | Evidence and result |
| --- | --- |
| Original/hearing-impaired retention during re-encoding | Live quality conversion retained flagged original audio, original subtitles and hearing-impaired subtitles, including their disposition flags. Unwanted ordinary audio was removed. Outputs decoded and originals restored by hash. Metadata-only and unknown-original fixtures retained all audio safely on this 10.11 host. |
| Re-encoding consent and commit-time policy | Five live FFmpeg-window changes—Off, DetectOnly, disabled list, protection and newly retained language—prevented replacement. Every change was observed while the temporary output existed; every source remained byte-identical. Retained-track inventory checks also passed. |
| Hidden/system links and inaccessible cross-volume inventory | Owned Windows filesystem regression checks passed, including hidden/system junction refusal and preservation of source/target. This machine provided no second filesystem volume, so an actual HTTP move between two physical volumes was not performed. |
| Partial repairs in totals | A damaged-secondary-audio Matroska fixture with an overstated duration header produced a successful partial-repair row. Lifetime and 30-day count and saved-byte deltas matched the recycled original; output decoded and original restored exactly. Genuinely truncated MP4/Matroska fixtures were safely recycled when replacement verification failed. |
| Original picker/UI lifecycle | Browser checked all 47 options, Original selection in both pickers and unsaved selections surviving tab navigation. Files context-menu manual Quality creation succeeded. Protection blocked a subsequent manual action with the expected message; the owned protection rule was removed. Fresh UI regression/lifecycle checks passed. |
| Seek-repair output format | Live detection → approval → repair → rescan completed on a generated 620-second broken-layout Matroska file; the healthy layout control stayed clean. Output and the five-minute FFmpeg seek segment decoded with audio/video. Complete Jellyfin HTTP delivery matched the output hash. |
| Combined cleanup authorization | Live cases covered both categories approved, subtitle Off, DetectOnly, disabled and protected. Unauthorized subtitle cleanup retained both subtitles and left its approval unresolved while authorized audio cleanup proceeded. Three FFmpeg-window changes—revoked subtitle approval, Off and protection—prevented the combined replacement and preserved the exact source. |
| Protected duplicate sidecars | Recycle and permanent disposal retained the protected NFO and containing folder, preserved the keeper hash and removed ordinary sidecars. Recycle restoration reproduced the exact media hash. Permanent deletion was confined to an owned generated duplicate. |
| Incomplete interleave evidence | Real-process executable probes and focused regressions returned Indeterminate for successful empty packet output and incomplete sample/full-walk evidence. Invalid durations/nonfinite timestamps were refused. Live healthy/broken layout controls behaved correctly. The server's global ffprobe executable was not replaced to inject failures. |

## Browser playback and additional fixes

On the final complete feature run, browser playback of the repaired seek fixture resumed at **300.297 seconds** and advanced to **331.053 seconds**, with `readyState=4`, `paused=false`, `muted=false` and no media error. The generated timestamped picture was visible after seeking. This proves browser playback progression; the separate FFmpeg checks establish decodable video and audio. Playback was ended before restoring the fixture.

The damaged-secondary-audio fixture exposed a mismatch: the scanner inspected all audio tracks, while fix-time revalidation and output verification inspected FFmpeg's default track. A shared `FindBrokenAudioStreamAsync` check now serves all three paths. The corrected live run dropped the damaged secondary audio, retained healthy audio and identical decoded video, cleared the issue on rescan, matched full Jellyfin delivery, preserved repair accounting and restored the original hash.

Partial-repair reporting now says that playable duration is shorter than originally declared. It no longer claims that the difference proves unrecoverable tail footage was trimmed. The accounting fixture deliberately combines damaged secondary audio with an overstated header; its healthy video packets must remain intact.

## Evidence, isolation and preliminary failures

Evidence is retained under `C:\dev\ship-review-20261010`:

- `live-repair`: first broad repair matrix, **270 passes/12 failures**. It exposed the secondary-audio bug; a refused queued fixture also blocked the header case through the harness's queue-isolation assertion. Truncated-MP4 partial-repair expectations failed because safe output verification rejected that repair. These results remain preserved.
- `live-audio-retest`: corrected secondary-audio/header/healthy retest, **66 passes/zero failures**, including dry-run, full delivery, rescan, exact restore and cleanup.
- `live-session-features`: preliminary short-fixture feature run, **102 passes/27 failures**. Eight timing windows were missed because Windows `localhost` fallback cost approximately two seconds per request and fixtures lasted only two seconds. It also expected a technical word absent from the plain-language seek history message and expected a partial repair from a fixture safely refused by verification. All eight converted timing-fixture originals were subsequently restored exactly.
- `live-session-final`: complete corrected feature run, **130 passes/zero failures**, eight-second fixtures through `127.0.0.1` on the same host, plus retained-flag, browser-seek and final-fixture evidence.
- `live-final-message`: focused acceptance of the final wording-only DLL, **17 passes/zero failures**, with all 23 original fixture hashes verified afterward.
- `live-preflight`: original settings/queue/library/protection snapshots, original/candidate DLL backups, browser manual-action evidence, deployment identities and final restoration verification.

Every Fix task was restricted to explicitly approved owned fixtures. The original 11 approvals were held as Dismissed and restored. Harness history queries verified no writes outside their own fixture roots. The shared recycle bin and history were not purged; owned fixture/evidence files remain available. Temporary library registrations and protection rules were removed. Settings, enabled libraries, all 11 approval identities and the original library/protection registrations were compared with their predeployment snapshots. Owned originals were verified by hash, except the explicitly tested permanent duplicate disposal; no abandoned repair/recovery files remained.

## Release boundary

**Do-not-push as a release certification yet.** The requested local acceptance work passes within the evidence boundaries above. The follow-up below adds local AMD hardware coverage. Execution on Jellyfin 12's inherited-language API, Linux/Docker/macOS, other GPU families, another real filesystem volume, physical Android/TV clients, disk-full/crash recovery and large-library resource limits remains unverified. The local server remains running with the final build and restored user configuration.

## Release-gap follow-up

The skipped source-identity placeholder is now a real integration test through `FormatCompatibilityFixer`, an owned converter and `VerifiedReplacementService` using the actual filesystem. Separate length and timestamp changes during conversion are refused with `SourceChanged`; the changed source bytes survive, no destination or recycled original is created, and temporary output is removed. An unchanged-source control produces a valid EPUB and recycles the original. The full Release suite now passes **1,525 tests with zero skips** (`release-gaps-20261010.trx`).

The Docker smoke workflow requested `MediaDashConfigPage`, which is the HTML element ID rather than the registered page name. It now requests `MediaDash`. Authenticated requests against the running local host returned **200** for the corrected URL and **404** for the old URL. Docker execution itself was not performed on this Windows host.

A further smoke-test pass confirmed the same URL result without authentication. The log checks now use Bash here-strings: with `pipefail`, the previous `echo | grep -q` pipeline rejected a successful load when the log exceeded pipe capacity. The page request now uses the runner's curl and asserts MediaDash's HTML element, avoiding a dependency on curl inside the Jellyfin image and refusing a generic HTTP-200 page. The actual workflow verification block was exercised under WSL Bash with injected Docker/curl responses: large healthy logs passed, while load errors, missing successful-load records and wrong page content failed. The previous pipeline's false failure was reproduced separately. Evidence: `release-gaps/smoke-checks.json`. These are workflow checks; Docker startup remains unverified locally.

Direct two-second file encodes using both `h264_amf` and `hevc_amf` succeeded on this machine's AMD graphics (`release-gaps/gpu-probes.json`). A separate HEVC lavfi-to-null-sink probe stalled at the first frame and was stopped; successful file encodes do not establish that all FFmpeg sink modes work. The local plugin quality flow was then exercised with hardware encoding enabled for original-flagged, metadata-only and unknown-original fixtures: **28 assertions passed, zero failed**. All three output video streams identified `h264_amf`; expected language tracks survived, outputs fully decoded and originals restored exactly. Settings, all 11 approvals, library registrations and protection rules were restored; all 23 fixture hashes and absence of temporary repair files were verified (`final-verification.json`). Evidence is in `live-gpu-final-02`; the initial attempt safely refused to start while the restoration rescan was active (`live-gpu-final`).
