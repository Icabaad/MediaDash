# MediaDash Media Organiser — Design Spec

**Status**: Draft — pending user review
**Date**: 2026-09-01
**Replaces**: `MediaGrouperScanner` + `MediaGrouperFixer` (Ungrouped), `MediaSorterScanner` + `MediaSorterFixer` (Misplaced)
**Ship gate**: 100% real-world tested + fuzzed + user-signed off before release notes claim "working"

---

## 1. Goal

Replace the existing MediaGrouper (Ungrouped) and MediaSorter (Misplaced) scanners with a single unified **Media Organiser** that ensures every identified Movie / TV Episode / Anime Movie / Anime Episode in the user's enabled Jellyfin libraries sits at its canonical Jellyfin-conventional path (correct folder AND correct filename). Files Jellyfin hasn't identified are surfaced to the Errors tab with an inline manual-rename affordance rather than guessed at.

---

## 2. Scope & boundaries

### In scope

- Movies + TV Episodes + Anime Movies + Anime Episodes in libraries the user has enabled (`PluginConfiguration.EnabledLibraries`). Anime movies and anime episodes are routed independently via optional target-path overrides — see §5.3.
- Files Jellyfin has confidently identified (has provider IDs / year for movies; series identity + season + episode for TV).
- Atomic move + rename + folder creation.
- Sidecar migration (subtitles, `.nfo`, artwork) alongside the primary file.
- History entry per rename; existing Restore-from-History logic reverses the move.
- Errors-tab surface for unidentified files, one entry per file, each with an inline `Rename` button that opens a modal to enter identification manually.

### Out of scope for this ship

- Music, Audiobooks, Books, Comics, Pictures (existing behavior retained).
- Watching a source / Downloads folder (mediasorter's source→library flow — deliberately never in scope).
- Direct TMDB / TVDB / AniDB / Shoko HTTP calls from MediaDash to enrich missing metadata. Jellyfin (and its AniDB / Shokofin plugins) already tried — we don't second-guess. Provider IDs those plugins populated on `item.ProviderIds` are fair game to read (§5.3.3); making our own HTTP requests to those services is not.
- Multi-season merges (e.g. re-numbering a season because a provider changed the ordering) — Jellyfin handles this itself.
- Deleting files. Organiser only moves + renames; existing OrphanCleanup handles genuine orphans.

### Invariants (locked; enforced by tests)

1. Never moves a file outside its Jellyfin library root (`LibraryGuard.IsInsideLibrary` on source and target).
2. Never overwrites an existing file at the target path.
3. Never touches a file Jellyfin didn't identify with sufficient confidence (Section 4.2).
4. Rename is atomic per file — either fully done + history recorded, or nothing changed.
5. Sidecars either all move with the primary, or the primary rolls back.
6. **Jellyfin userdata (watched state, play position, favorite, rating, playlist membership) is preserved across every move** via `ILibraryManager.UpdateItemAsync` so item identity stays continuous.
7. **Touched library roots get a targeted refresh after the fix batch** — `ILibraryMonitor.ReportFileSystemChanged` per changed folder plus `ILibraryManager.ValidateMediaLibrary` per affected root.

---

## 3. Architecture

Approach A from brainstorming: **new unified `MediaOrganiser` scanner + fixer**, retire the old two.

```
┌─────────────────────────┐
│  MediaOrganiserScanner  │  iterates BaseItem[] filtered by EnabledLibraries
│  (IScanner)             │  emits IssueType.Ungrouped where item.Path != canonical,
│                         │  OR diagnostic-only when rules refuse to guess (§5.2.1 r.3, §4.4)
└──────────┬──────────────┘
           │ Issue { Path, DetailsJson={target, kind, itemId, providerIds} }
           ▼
┌─────────────────────────┐
│  MediaOrganiserFixer    │  atomic move + rename + sidecar + Jellyfin item update
│  (IFixer)               │  writes History entry with source→target
└──────────┬──────────────┘
           │
           ▼
┌─────────────────────────┐
│  FixTask end-of-batch   │  ValidateMediaLibrary per touched root
└─────────────────────────┘
```

Pure functions extracted for exhaustive testing:
- `CanonicalNaming.SanitiseComponent(string) → string` — filesystem-safe path component
- `CanonicalNaming.MoviePath(moviesRoot, name, year, ext) → string` — also used for Anime Movies with the anime root passed in
- `CanonicalNaming.EpisodePath(tvRoot, seriesName, season, episode, endEpisode?, ext) → string` — also used for Anime Episodes with the anime root passed in

The rest of the code composes these. Anime doesn't get bespoke pure functions — same shape, different root argument (§5.3.2).

---

## 4. Detection logic

### 4.1 Scanner walk

The scanner receives `IReadOnlyList<BaseItem>` from `ScanTask`. It:

1. Reads target roots from config: `MoviesTargetPath`, `TvTargetPath`, `AnimeTargetPath`, `AnimeShowsTargetPath`, `AnimeMoviesTargetPath`. If none of the five is configured, emits no issues (idle no-op).
2. Filters items to `Movie`, `Episode` (with or without Anime genre tag) whose `Path` is inside an enabled library.
3. For each item, extracts identification fields (Section 4.2), classifies media kind (Section 4.3), computes canonical target (Section 5).
4. If canonical target ≠ current path AND target path does not already exist AND source is inside a library: emits `Issue { Type=Ungrouped, Path=item.Path, DetailsJson={action, target, mediaKind, itemId, providerIds} }`.
5. If item cannot be identified: emits a `MediaOrganiser.Unidentified` diagnostic to the Errors tab instead (Section 6).

### 4.2 Identification requirements

| Kind          | Required fields                                                                 |
|---------------|---------------------------------------------------------------------------------|
| Movie         | `Name`, `ProductionYear`, at least one `ProviderId`                             |
| Episode       | `SeriesName`, `ParentIndexNumber` (season), `IndexNumber` (episode)             |
| Anime Movie   | Same as Movie + is-anime signal (see §5.3.3)                                    |
| Anime Episode | Same as Episode + is-anime signal (see §5.3.3)                                  |

Anything missing any required field is unidentified.

### 4.3 Media kind classification

**Anime detection** (used by the routing rules below — see §5.3.3 for detail). An item is anime if any of the following is true, first hit wins:

1. `item.ProviderIds` contains any of `AniDb`, `Shoko Series`, `Shoko Episode` (case-insensitive lookup).
2. Item has the Anime genre (`AnimeDetection.HasAnimeGenre` — helper extracted from the deprecated `MediaSorterScanner` into a shared utility before that class is deleted; see §8.3).

**Routing** (priority order — first match wins):

1. If item is anime AND item is `Movie` AND (`AnimeMoviesTargetPath` or fallback `AnimeTargetPath`) configured → **Anime Movie**.
2. If item is anime AND item is `Episode` AND (`AnimeShowsTargetPath` or fallback `AnimeTargetPath`) configured → **Anime Episode**.
3. If item is `Movie` AND `MoviesTargetPath` configured → **Movie**. (Anime movie with no anime destination configured falls through here — deliberate, so users who ignore the anime split still get organised into their general Movies library.)
4. If item is `Episode` AND `TvTargetPath` configured → **TV**. (Same fallback logic as rule 3.)
5. Otherwise → skip (no target root configured for this kind).

### 4.4 Collision handling

Two distinct collision cases — both refuse to emit and log a diagnostic instead of queuing an issue that could never safely succeed:

1. **Physical target collision.** If the computed target already exists on disk (some other file at that path), log `MediaOrganiser.Collision` naming source + target. Fixer can never succeed; better to surface the manual conflict once.
2. **Cross-source series collision.** If two proposals in the same scan target the same series folder from different source parent-of-parent folders (see §5.2.1), refuse to emit both. Diagnostic variant depends on the evidence available:
   - **Anime with differing provider IDs** — when both items' `Series.ProviderIds[AniDb|Shoko Series]` are populated and differ (see §5.3.3), log `MediaOrganiser.AnimeRebootCollisionRefused` and include both IDs in the message. Provably-distinct anime series never merge, even when Jellyfin's TVDb match collapsed their `SeriesName`.
   - **All other cases** — log `MediaOrganiser.RebootCollisionRefused` naming both sources. Safety net when reboot disambiguation misses (e.g. two folders both bare "Doctor Who" with no year signal on disk and no `PremiereDate` in Jellyfin).

---

## 5. Canonical naming rules

Following Jellyfin's documented conventions (`https://jellyfin.org/docs/general/server/media/`).

### 5.1 Movie

```
{MoviesRoot}/{SafeName} ({Year})/{SafeName} ({Year}).{ext}
```

- `SafeName` = `SanitiseComponent(item.Name)`.
- `Year` = `item.ProductionYear`.
- Extension preserved verbatim from source (lowercased).
- Example: `/media/movies/Blade Runner (1982)/Blade Runner (1982).mkv`.

### 5.2 TV Episode

```
{TvRoot}/{SafeSeries}/Season {NN}/{SafeSeries} S{NN}E{NN}.{ext}
```

- `SafeSeries` = `ResolveSafeSeries(episode)` (see 5.2.1 — NOT the plain scrub of `episode.SeriesName`).
- `NN` = zero-padded 2-digit season / episode number.
- **Multi-episode files**: if `episode.IndexNumberEnd` is set and > `IndexNumber`, filename becomes `{SafeSeries} S{NN}E{NN}-E{NN}.{ext}` (Jellyfin's recognised range form).
- **Specials** (season 0): folder is `Season 00`, filename `Show S00E01.mkv`.
- Example: `/media/tv/Breaking Bad (2008)/Season 03/Breaking Bad (2008) S03E07.mkv`.

### 5.2.1 Reboot disambiguation (`ResolveSafeSeries`)

The legacy `MediaGrouperScanner` used `SanitiseComponent(episode.SeriesName)` verbatim — the code path behind GitHub issue #43 (Doctor Who reboots + Silo (2023) year-strip, reported by @hesourman and confirmed by @Vaygrim). See the standalone reproduction at `tools/comprehensive-test/simulate-doctor-who/` for a live demo.

**Backport status:** the resolver below was backported to `MediaGrouperScanner.ResolveSafeSeriesName` in the 1.0.7.6 bugfix (see CHANGELOG). The Media Organiser MUST reuse the same rule order — moving the pure function into `CanonicalNaming.ResolveSafeSeries` and delegating from both call sites keeps them in lock-step so a fix or edge-case discovered in one doesn't drift from the other. Regression coverage lives in `MediaGrouperRebootTests` and any Media Organiser layer-1 tests must include the same InlineData rows plus the cross-source collision-refused case.

**Problem:** Jellyfin's default TVDb match collapses `Doctor Who (1963)`, `Doctor Who (2005)`, `Doctor Who (2024)` into a single Series entity with `SeriesName = "Doctor Who"`. If the organiser trusts `SeriesName` alone, all three reboots propose to merge into one `Doctor Who/` folder, colliding every `Season NN/` subfolder across reboot years — destructive.

**Rule:** `ResolveSafeSeries` picks the first strategy that produces a non-ambiguous name, in order:

1. **On-disk disambiguation** (highest priority — preserves what the user already did):
   - Read the source's parent-of-parent folder name (e.g. `Doctor Who (2005)` for `.../tv/Doctor Who (2005)/Season 01/E01.mkv`).
   - If it matches `^(.+?)\s*\((\d{4})\)\s*$`, use `SanitiseComponent(parentName)` as `SafeSeries`. Done.
   - This preserves the user's manual disambiguation even when Jellyfin's metadata collapses.

2. **Jellyfin-metadata disambiguation** (fallback when parent folder is bare):
   - If `episode.Series.PremiereDate` is populated, `SafeSeries = SanitiseComponent(episode.SeriesName) + " (" + Year + ")"`.
   - Matches Jellyfin's own recommended folder-naming convention.

3. **Refuse to guess** (last resort — when neither year signal exists):
   - Do NOT emit an Issue. Emit a `MediaOrganiser.UndisambiguatedSeries` diagnostic naming the file to the Errors tab (with the standard `Rename` affordance from §6.2).
   - The organiser never places a file into a bare `Doctor Who/` (or any potentially-collapsed) folder without user confirmation. Costs one Rename click per un-disambiguable series but eliminates the class of bug where a later reboot silently merges into an earlier one.
   - Fallback-if-unsure principle: whenever the resolver cannot pick a disambiguator with confidence, the file surfaces for user review rather than a silent guess.

**Cross-source collision guard (belt-and-braces):** even after `ResolveSafeSeries`, if two proposals in a single scan would target the same series folder from different source parent folders (measured by canonical `Path.GetFullPath` on the parent-of-parent), refuse to emit both. See §4.4 case 2 for the diagnostic variants (plain `RebootCollisionRefused`, or `AnimeRebootCollisionRefused` when anime provider IDs differ).

### 5.2.2 Layout examples

Traces of what the organiser does for the common on-disk arrangements. **In every case where the rules cannot confidently pick a disambiguator, the file is not moved — it surfaces on the Errors tab with a `Rename` affordance (§6) for the user to resolve manually.** Silent guessing is never acceptable when reboots are in play.

**Layout A — Clean, all year-tagged (best case)**

```
/tv/Doctor Who (1963)/Season 01/S01E01.mkv
/tv/Doctor Who (2005)/Season 01/S01E01.mkv
/tv/Doctor Who (2024)/Season 01/S01E01.mkv
```

Rule 1 hits for every file; each parent-of-parent matches `(YYYY)`. Result: three distinct `SafeSeries`, three distinct target folders, files stay in their own year folders — only the filename is rewritten to `Doctor Who (YYYY) S01E01.mkv`. No merge. No user action.

**Layout B — Bare + year-tagged mix (collapse trap)**

```
/tv/Doctor Who/Season 01/S01E01.mkv
/tv/Doctor Who (2005)/Season 01/S01E01.mkv
```

Both files matched by Jellyfin to the same TVDB entry (2005). Rule 1 hits for the year-tagged folder (`"Doctor Who (2005)"`) and misses for the bare folder (falls to rule 2, which — because both matched the same series — also produces `"Doctor Who (2005)"`). Two proposals target the same series folder from different source parents → **cross-source collision guard fires**. Both files stay put; `MediaOrganiser.RebootCollisionRefused` diagnostic naming both sources goes to the Errors tab with `Rename` on each entry. User picks which is which. No silent merge.

**Layout C — Single bare folder, no reboot on disk**

```
/tv/Doctor Who/Season 01/S01E01.mkv
/tv/Doctor Who/Season 02/S02E01.mkv
```

Rule 1 misses (bare parent). Rule 2 hits — Jellyfin has `PremiereDate` on whichever entry it matched (likely the 2005 revival) → `SafeSeries = "Doctor Who (2005)"`. Files move into `Doctor Who (2005)/`. Legitimate action: no other reboot is on disk to collide with. `OrganiserFixMode` defaults to `DetectOnly`, so the proposed rename appears on the Fixes tab for user review before anything moves; if the user actually meant the 1963 series they catch it there.

**Layout D — Reboot years buried at the season level**

```
/tv/Doctor Who/Season 01 (1963)/S01E01.mkv
/tv/Doctor Who/Season 01 (2005)/S01E01.mkv
/tv/Doctor Who/Season 01 (2024)/S01E01.mkv
```

Parent-of-parent is bare `Doctor Who` for every file. Rule 1 misses. Rule 2 → same `SafeSeries` for every file. Every file's canonical target becomes the same path → **filename collisions**. Physical-target-collision check (§4.4.1) refuses each proposal after the first. Nothing moves; three `MediaOrganiser.Collision` diagnostics on the Errors tab, each with `Rename` for explicit placement.

**Layout E — Flat, no series folder**

```
/tv/Doctor Who S01E01.mkv
/tv/Doctor Who (2005) S01E01.mkv
```

The source's parent IS the library root — no parent-of-parent to inspect. Rule 1 skips. Rule 2 attempts `SafeSeries` from `SeriesName + PremiereDate`. If Jellyfin matched both files to the same entry, cross-source collision fires at the file level (both propose the same target) → both refused. If Jellyfin distinguished them (e.g. the filename year hinted TVDB toward the 2005 entry) each gets a distinct target and moves cleanly.

**Layout F — Neither rule can disambiguate**

```
/tv/Doctor Who/Season 01/S01E01.mkv     (SeriesName="Doctor Who", PremiereDate unset)
```

Rule 1 misses (bare parent). Rule 2 misses (no `PremiereDate`). **Rule 3 refuses to emit a proposal.** File surfaces on the Errors tab as `MediaOrganiser.UndisambiguatedSeries` with `Rename` — user supplies series year via the manual-rename modal, canonical target is computed from their input, fix proceeds via the §6.4 endpoint.

Skipped for brevity: anime layouts (same shape as TV, addressed in §5.3) and movie reboots (Movies carry `ProductionYear` on the item itself, not a shared Series entity, so the collapse problem doesn't exist).

### 5.3 Anime

Anime is treated as a first-class category, split into **Anime Movies** and **Anime Shows** with independent destinations. Detection prefers AniDB / Shokofin provider IDs when present (stronger signal than genre tag), and the same reboot-collision protections that apply to live-action TV (§5.2.1) apply to anime series with an AniDB/Shoko-keyed variant on top.

#### 5.3.1 Routing target

For each anime item, target root resolves as:

- **Anime Movie** target root = `AnimeMoviesTargetPath` ?? `AnimeTargetPath`.
- **Anime Episode** target root = `AnimeShowsTargetPath` ?? `AnimeTargetPath`.

`AnimeTargetPath` (the legacy single-bucket setting) is retained as fallback so existing installations keep working with zero migration. Users opt in to the split by setting either or both of the new fields. If none of the three is set for a given anime kind, routing falls through to §4.3 rule 3/4 (the general Movies/TV library).

#### 5.3.2 Canonical path shapes

**Anime Movie** — same shape as §5.1 Movie:

```
{AnimeMoviesRoot}/{SafeName} ({Year})/{SafeName} ({Year}).{ext}
```

**Anime Episode** — same shape as §5.2 TV:

```
{AnimeShowsRoot}/{SafeSeries}/Season {NN}/{SafeSeries} S{NN}E{NN}.{ext}
```

Multi-episode range formatting (`S01E01-E02`), specials handling (`Season 00`), and reboot disambiguation via `ResolveSafeSeries` (§5.2.1) all apply identically. Anime does not get a bespoke naming shape — Jellyfin's own conventions cover it.

#### 5.3.3 Anime provider IDs (AniDB / Shokofin)

MediaDash relies on the Jellyfin **AniDB metadata plugin** and/or **Shokofin** (both are Jellyfin plugins that populate `item.ProviderIds` automatically — no external service call from MediaDash) to identify anime. When either plugin is installed and active, an anime item carries one or more of:

- `AniDb` — series/episode ID from the community AniDB plugin.
- `Shoko Series` — Shokofin series ID.
- `Shoko Episode` — Shokofin episode ID.

**Key-casing note:** both plugins have shipped multiple casings historically (`AniDb`, `AniDB`, `anidb`). Lookup code must be case-insensitive. Verify actual keys during implementation by inspecting `item.ProviderIds.Keys` on a dev instance with each plugin installed.

The organiser uses these IDs three ways:

**(a) Anime detection (§4.3).** Presence of any anime provider ID is the strongest is-anime signal — stronger than genre tag, because Jellyfin's genre extraction from AniDB metadata is inconsistent. If the ID is present, the item is anime even when the Anime genre tag is missing or reversed.

**(b) Collapse detector for anime series reboots (§4.4 case 3).** Two anime proposals targeting the same series folder from different source parent folders but with differing `Series.ProviderIds[AniDb|Shoko Series]` are provably distinct series — refuse to emit both, log `MediaOrganiser.AnimeRebootCollisionRefused` naming both sources and both IDs. This is the anime-native version of §5.2.1's cross-source guard: Jellyfin's TVDb matcher may collapse a franchise (e.g. the Fate/, Monogatari, or Gundam families) into one `SeriesName`, but AniDB's per-series IDs never do.

**(c) Refuse-to-guess extension for anime.** If an anime item lacks any anime provider ID AND its `SeriesName` is bare (no year, no distinguishing suffix), fall back to §5.2.1 rule 3 (refuse to emit, surface to Errors tab with `Rename`). Extends the fallback-if-unsure principle to anime: without a strong ID and without a distinguishing name, we don't guess.

#### 5.3.4 Opt-in anime ID suffix ("Keep AniDB / Shoko ID in canonical name")

Mirrors §5.6's setting model. When enabled, the canonical anime filename gains a trailing tag:

- **Anime Movie**: `{SafeName} ({Year}) [anidb-{N}].{ext}` (or `[shokoseries-{N}]` if only Shoko ID is present).
- **Anime Episode**: `{SafeSeries} S{NN}E{NN} [anidb-{N}].{ext}`.

**Source priority for the ID:**

1. Existing `[anidb-N]` / `[shokoseries-N]` / `[shokoepisode-N]` tag in the source filename (users of Shokofin's own file naming).
2. `item.ProviderIds[AniDb]` if present.
3. `item.ProviderIds[Shoko Series]` (or `Shoko Episode` for episodes) if AniDB missing.

If none yields an ID, emit the plain canonical name (no dangling `[anidb-]` marker) and log `MediaOrganiser.MissingAnimeProviderId` naming the file.

**Determinism guard** identical to §5.6: adding the tag must keep `ComputeTargetPath` idempotent — running the organiser twice with the setting on must not toggle the tag on and off. Turning the setting off cleanly re-canonicalises the tag away on next run.

**Regression test**: fixture Shokofin-named `.mkv` (`Show S01E01 [anidb-12345].mkv`) survives an organiser rename with the setting on; without the setting, the tag is dropped. Both paths asserted in the Layer 3 corpus.

### 5.4 Sanitisation rules (`SanitiseComponent`)

- Strip Windows-reserved chars `< > : " / \ | ? *` → replace with hyphen `-`.
- Strip control chars (`\x00-\x1F`).
- Collapse consecutive whitespace to a single space.
- Trim trailing dots and spaces (Windows disallows these on directory names).
- Refuse to produce an empty string — fall back to `_untitled` if all input stripped.
- Refuse Windows-reserved DOS names (CON, PRN, AUX, NUL, COM1-COM9, LPT1-LPT9) — append `_` if the input matches.
- Preserve Unicode letters, digits, punctuation that aren't in the reserved set (émoji, CJK, cyrillic all fine).
- Pure function — no I/O, deterministic, testable in isolation.

### 5.5 What is NOT in the filename

Deliberately omitted for the 100% bar:

- Episode titles (subject to change on metadata refresh)
- Quality tags (`1080p`, `x265`)
- Release group tags (`-GROUP`)
- Codec tags
- Audio/subtitle language codes on the primary file (sidecar `.en.srt` naming stays as-is)

Rationale: the canonical target must be deterministic given `(item.Id, config)`. Anything that changes across metadata refreshes would cause re-detection loops.

### 5.6 Provider ID tag preservation (opt-in)

The **Files Wasting Space** fixer already exposes the **"Keep TMDB / TVDB ID in the canonical name"** setting (added for the Transcode fixer's canonical rename — see CHANGELOG). When that setting is ticked, the organiser MUST honour it too: the canonical filename gains a trailing `[tmdbid-N]` (movies) or `[tvdbid-N]` (episodes) tag, matching Sonarr / Radarr default naming so those tools don't re-flag the file as unknown after an organiser rename.

- **Movie**: `{SafeName} ({Year}) [tmdbid-{N}].{ext}` — folder stays `{SafeName} ({Year})/`.
- **Episode**: `{SafeSeries} S{NN}E{NN} [tvdbid-{N}].{ext}` (or `[tmdbid-{N}]` if that's the provider Jellyfin actually resolved).
- **Source of the ID**: prefer an existing `[tmdbid-N]` / `[tvdbid-N]` tag already in the source filename (Sonarr / Radarr users). Fall back to `item.ProviderIds` from Jellyfin. If neither yields an ID, emit the plain canonical name (no dangling `[tmdbid-]` marker) and log `MediaOrganiser.MissingProviderId` naming the file.
- **Determinism guard**: adding the tag must keep `ComputeTargetPath` idempotent — running the organiser twice with the setting on must not toggle the tag on and off. The tag is part of the canonical target when the setting is on, and part of what `SanitiseComponent` strips when the setting is off (so turning the setting off cleanly re-canonicalises away the tag on next run).
- **Regression test**: a fixture Sonarr-style `.mkv` (`Show S01E01 [tvdbid-12345].mkv`) survives an organiser rename with the setting on; without the setting, the tag is dropped. Both paths asserted in the Layer 3 corpus.

---

## 6. Unidentified-file UX

### 6.1 Errors-tab surface

For every file that reaches the scanner but fails identification, emit one Diagnostic:

- **Source**: `MediaOrganiser.Unidentified`
- **Message** (Movie):
  ```
  {path}: MediaDash couldn't organise this file because Jellyfin hasn't identified it.
  Jellyfin expects movies at 'Movie Name (Year)/Movie Name (Year).ext' inside your Movies library.
  Click Rename to enter the title and year manually.
  ```
- **Message** (Episode):
  ```
  {path}: MediaDash couldn't organise this file because Jellyfin hasn't identified the show, season, and episode.
  Jellyfin expects TV at 'Show Name/Season 01/Show Name S01E01.ext' inside your TV library.
  Click Rename to enter the details manually.
  ```

Diagnostics are already deduped by source + message hash server-side, so re-scanning doesn't multiply entries.

### 6.2 Inline manual-rename button

The Errors tab already renders per-source buttons (precedent: the "Merge into current bin" button on legacy-batch rows). The rendering layer switches on source prefix to inject the `Rename` button on any `MediaOrganiser.Unidentified` entry.

### 6.3 Manual-rename modal

Opens on Rename click. The media kind is passed from server to client via a new `Metadata` string field on `DiagnosticEntry` (JSON: `{"kind":"Movie"}` or `{"kind":"Episode","seriesHint":"..."}`). Requires a small, backwards-compatible extension to the Diagnostics DTO. Modal adapts to kind:

| Field         | Movie | Episode |
|---------------|-------|---------|
| Title         | ✓     | ✓ (series name) |
| Year          | ✓     | ✗       |
| Season number | ✗     | ✓       |
| Episode number | ✗    | ✓       |
| Episode-end (multi-episode) | ✗ | optional |
| Series year (resolves Layout F / B refusals — builds `SafeSeries = "{Title} ({year})"`) | ✗ | optional |
| Kind toggle (Movie / TV / Anime Movie / Anime Episode) | ✓ (defaults per message; anime split routes to `AnimeMoviesTargetPath` or `AnimeShowsTargetPath` per §5.3.1) |
| Preview: computed target path | ✓ | ✓ |

### 6.4 Manual-rename endpoint

- **Route**: `POST /MediaDash/Organise/ManualRename`
- **Body**: `{ sourcePath, kind, title, year?, season?, episode?, episodeEnd?, seriesYear? }`. `kind` accepts `Movie` / `TV` / `AnimeMovie` / `AnimeEpisode` — the last two route to the anime target paths per §5.3.1.
- **Behaviour**: computes canonical target from the user-provided fields (same `CanonicalNaming` functions), applies steps 3–13 of the fixer flow (Section 7) plus step 15 (write History entry) so the manual rename is Restorable via §7.2. Step 14 (repoint queued sibling issues) is skipped — manual renames have no queued issue to repoint. Response: `{ success, target?, error? }`.
- **Auth**: same admin-only bracket as every other MediaDash mutation endpoint.

The manual-rename endpoint is the **only** code path that bypasses the "must be Jellyfin-identified" gate. The user is providing the identification manually, so we honor it. Jellyfin's own identifier can re-match the file post-rename via the canonical folder structure.

---

## 7. Fixer flow

`MediaOrganiserFixer.FixAsync(issue, progress, cancellationToken)`:

1. **Re-check source** — `File.Exists(issue.Path)` and item still identified. Bail cleanly if not.
2. **Recompute target** — config or metadata may have changed since scan. Bail if computed target now matches source (nothing to do).
3. **LibraryGuard** — `IsInsideLibrary(source)` and `IsInsideLibrary(target)`. Bail with clear error if either fails.
4. **Collision check** — `File.Exists(target)` → fail with "target already exists" (stale failure — surfaces as `MediaOrganiser.Collision` diagnostic; the `IsStaleFailure` heuristic in FixTask marks it Fixed on first failure to stop the retry loop).
5. **Cross-volume free-space check** — reuse pattern from existing `MediaSorterFixer`.
6. **Dry-run gate** — return `FixResult.DryRun(actionText)` if `config.DryRun`.
7. **Capture timestamps** — `srcCreatedUtc`, `srcModifiedUtc` (WIP timestamp preservation pattern).
8. **Create target directory** — `Directory.CreateDirectory(targetDir)`.
9. **Physical move**:
   - Same-volume: `File.Move(source, target)` (atomic).
   - Cross-volume: `File.Move(source, stagingPath)` then `File.Move(stagingPath, target)`. Staging path uses `.mediadash.tmp.{guid}` suffix so `SweepOrphanSidecars` cleans up on crash.
10. **Restore timestamps** — `File.SetCreationTimeUtc` / `SetLastWriteTimeUtc` on target. Best-effort.
11. **Update Jellyfin item identity** — critical for invariant #6:
    ```csharp
    item.Path = target;
    await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
    ```
    Keeps `item.Id` stable so all userdata rows (watched, played, favorite, rating, playlist) stay attached. If `UpdateItemAsync` throws, log a `MediaOrganiser.MetadataUpdateFailed` diagnostic and continue — the file is at the canonical path; worst case Jellyfin's own scan re-associates it on the next library validation (Section 7.1) and userdata reattaches by `ProviderId` match. Do NOT rollback the physical move on this failure — the file is already at its correct destination. This is the one intentional exception to invariant #4: physical move + history entry are both durable; the Jellyfin metadata sync is best-effort with a repair path.
12. **Move sidecars** — reuse `MoveSidecars` helper (extracted from `MediaSorterFixer` into a shared `SidecarMover` utility). Sidecar failures logged but don't fail the primary move.
13. **Notify Jellyfin per file** — `_libraryMonitor.ReportFileSystemChanged(source)` and `_libraryMonitor.ReportFileSystemChanged(target)`.
14. **Repoint queued sibling issues** — `_db.RelocateIssuePaths(source, target)`.
15. **Write History entry** — `{ IssueId, Path=source, Action="organised {source} → {target}", RecyclePath=target, WasDryRun=false, Success=true }`. Storing target in `RecyclePath` lets the existing Restore-from-History logic reverse the move (Restore treats it as "move back").

### 7.1 End-of-batch library refresh

Added to `FixTask.ExecuteAsync` after the main loop:

```csharp
var touchedRoots = organiserSuccesses.Select(TargetLibraryRoot).Distinct(...);
foreach (var root in touchedRoots)
{
    try
    {
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), recursive: true, cancellationToken);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "Post-organise library refresh failed on {Root}", root);
    }
}
```

Non-fatal on failure — Jellyfin's own scheduled scan picks it up eventually. The point is to make the user's next dashboard visit already reflect reality.

### 7.2 Restore-from-History

Existing Restore button in the History tab already looks for `RecyclePath` on the entry. For organiser entries, `RecyclePath` holds the new location and `Path` holds the original — Restore reverses the move (target → source). Requires updating the Restore endpoint to recognise "organiser" action prefix; then reuses the same move-with-guards logic.

---

## 8. Config & migration

### 8.1 New config fields

```csharp
// PluginConfiguration.cs
public FixMode OrganiserFixMode { get; set; } = FixMode.DetectOnly;

// Anime split (both optional; fall back to legacy AnimeTargetPath when empty — see §5.3.1)
public string AnimeShowsTargetPath { get; set; } = string.Empty;
public string AnimeMoviesTargetPath { get; set; } = string.Empty;

// Opt-in anime provider ID suffix in canonical filename (see §5.3.4)
public bool KeepAnimeProviderIdInFilename { get; set; } = false;
```

`OrganiserFixMode` defaults to `DetectOnly` — surfaces issues without moving anything until the user opts in on the Fixes tab. The two new anime target paths default empty (legacy `AnimeTargetPath` handles both kinds until the user splits). `KeepAnimeProviderIdInFilename` defaults off, mirroring §5.6's TMDB/TVDB toggle.

### 8.2 Boot migration

On plugin startup (`Plugin.RegisterServices` or equivalent), one-shot check:

- If legacy `UngroupedFixMode` OR `MisplacedFixMode` was `Automatic`, set `OrganiserFixMode = DetectOnly`. Do **not** carry over Automatic — the new fixer renames as well as moves, which is more aggressive than either predecessor.
- If legacy was `DetectOnly` or `Off`, set `OrganiserFixMode` to the same value.
- If legacy was already migrated (flag `_organiserMigrated = true` in config), no-op.
- No migration for `AnimeShowsTargetPath` / `AnimeMoviesTargetPath` — both default empty and the §5.3.1 fallback to legacy `AnimeTargetPath` preserves existing behaviour byte-for-byte. Users opt in to the split by editing settings.
- Save config once, log an INFO line: `"Media organiser: migrated from Ungrouped/Misplaced. Set to DetectOnly by default — enable manually on the Fixes tab after reviewing the plan."`

### 8.3 Deprecated code paths

- `IssueType.Ungrouped` — repurposed for the new organiser (keeps DB rows valid).
- `IssueType.Misplaced` — deprecated. Existing rows preserved for history; no new emission. Old scanner + fixer classes deleted.
- `MediaGrouperScanner`, `MediaGrouperFixer` — deleted.
- `MediaSorterScanner` — `HasAnimeGenre` extracted to a shared `AnimeDetection` utility class (referenced from §4.3), then scanner class deleted.
- `MediaSorterFixer` — sidecar-moving helper extracted to shared utility, then class deleted.

### 8.4 UI changes

- Fixes tab: existing "Ungrouped media" card relabelled to "Media organiser". Description updated.
- Fixes tab: existing "Misplaced files" card removed. Any users with a saved custom FixMode see the migration line in Diagnostics.
- Settings → Libraries: existing Movies / TV / Anime target-path fields stay put — same config drives the new scanner. **Below the existing "Anime library" field, add two optional overrides**: "Anime shows library (optional)" and "Anime movies library (optional)", each with helper text: "Leave blank to keep both kinds in the main Anime library above."
- Settings → Naming: alongside the existing "Keep TMDB / TVDB ID in the canonical name" checkbox (§5.6), add "Keep AniDB / Shoko ID in canonical anime filenames" (§5.3.4). Same on/off model, anime-scoped.

---

## 9. Testing strategy (the 100% bar)

Three layers, all required green before shipping.

### 9.1 Layer 1 — Unit + property tests

Pure functions in `CanonicalNaming`:

- `SanitiseComponent`: table-driven for every reserved char on Windows and POSIX, every reserved DOS name, control chars, unicode, extremely long strings.
- **Property**: sanitised output never contains any of `< > : " / \ | ? *`, never empty, never ends in `.` or ` `, never matches a reserved DOS name.
- **Property**: idempotent — `Sanitise(Sanitise(x)) == Sanitise(x)`.
- `MoviePath` / `EpisodePath`: table-driven for representative inputs, exercised with both live-action and anime roots (same pure functions, per §3 and §5.3.2).
- **Property**: `ComputeTargetPath(alreadyOrganisedItem)` returns its current path (no drift).
- Multi-episode range formatting: `S01E01-E02`, `S01E10-E15`, `S00E01-E02` (specials range).
- **Reboot disambiguation** (GitHub issue #43 regression guard):
  - `ResolveSafeSeries` on three episodes with `SeriesName = "Doctor Who"` and parent-of-parent = `Doctor Who (1963)` / `(2005)` / `(2024)` produces three distinct `SafeSeries` values.
  - Same three episodes with parent-of-parent bare "Doctor Who" (no year on disk) and `PremiereDate.Year = 1963` for all three → `MediaOrganiser.RebootCollisionRefused` diagnostic, no proposals emitted.
  - Standalone reproduction available at `tools/comprehensive-test/simulate-doctor-who/` — run before every ship to prove the property holds.
- **Anime AniDB/Shoko collapse detector** (§5.3.3, §4.4 case 3):
  - Two anime episodes both `SeriesName = "Fate"` (bare) from different parent folders (`.../anime/Fate Zero/...` and `.../anime/Fate stay night/...`) with different `Series.ProviderIds[AniDb]` → `MediaOrganiser.AnimeRebootCollisionRefused` naming both sources and both IDs; no proposals emitted.
  - Same two episodes but with identical `AniDb` IDs (Jellyfin genuinely thinks they're the same series) → cross-source guard from §5.2.1 handles it normally (`RebootCollisionRefused`, non-anime variant).
  - Case-insensitive ProviderId lookup: `AniDb`, `ANIDB`, `anidb` all resolve to the same ID.
- **Anime detection cascade** (§4.3): item with `ProviderIds[AniDb]` set but NO Anime genre tag → classified as anime; item with Anime genre but no provider ID → classified as anime; item with neither → not anime.
- **Anime routing split** (§5.3.1): with `AnimeMoviesTargetPath = "/anime/movies"` and `AnimeShowsTargetPath = "/anime/shows"`, `AnimeTargetPath = "/anime/legacy"` — anime Movie routes to `/anime/movies`, anime Episode routes to `/anime/shows`, legacy path never used. Clear one of the new fields → that kind falls back to legacy. Clear both → legacy handles both.

### 9.2 Layer 2 — Fuzz tests

xUnit theories with a `Random` generator seeded per-test:

- Random Unicode + special-char titles (including surrogate pairs, RTL text, combining diacritics).
- Random years (0 to 9999, negative, missing).
- Random episode numbers (0, 1, 99, 999, negative, missing).
- **Assertions**:
  - Never throws (any input handled gracefully).
  - Output path is always a valid path on the current OS (attempt `Path.GetFullPath` — must not throw).
  - Output path is deterministic (same input → same output across N iterations).
  - Output path is always under the provided root (never escapes via `..` or absolute path injection).

Fuzz batch: 1,000 iterations per property, seeded for reproducibility. `SanitiseComponent` has a bounded alphabet of interesting inputs — 1k converges on the same coverage 10k would give while keeping CI fast. Grow the batch only if a real bug proves the smaller run missed it.

### 9.3 Layer 3 — Real-world corpus + integration

**Real-world corpus**:

- Starting target: ~100 real filenames covering the pattern *families*, not exhaustive permutations. Grow the corpus only when a real user report proves a gap the smaller set missed.
- Categories: TV episode naming variants (S01E01, 1x01, 101, S1E1, dotted, spaced), movie naming variants (year in title, year at end, no year, remake year suffix), anime naming variants (fansub group tags, alternative titles), edge cases (extras, specials, multi-episode, samples).
- Stored in test resources; loaded via `[Theory]` `[MemberData]`. Each entry asserts the transform matches.

**Integration on the running Jellyfin dev server**:

- Fresh fixture library: 25+ real files across Movies (5), TV (10 spanning 2 seasons), Anime shows (5 across 2 series), Anime movies (3, at least one carrying an `AniDb` ProviderId and one carrying only `Shoko Series`), plus 3 external subtitle sidecars and 2 `.nfo` files.
- **Anime split assertion**: with `AnimeMoviesTargetPath` and `AnimeShowsTargetPath` both configured, anime movies land under the movies root and anime episodes land under the shows root — no cross-contamination. Repeat with only `AnimeTargetPath` configured (legacy fallback): both land under the single legacy root.
- **Anime provider-ID suffix assertion**: with `KeepAnimeProviderIdInFilename = true`, fixture `Fate stay night S01E01 [anidb-4879].mkv` survives rename with the tag preserved; with the setting off, the tag is stripped.
- **Reboot corpus:** 3 x `Doctor Who ({year})/Season 01/S01E01.mkv` (`1963`, `2005`, `2024`) — reproduces GitHub #43. Set Jellyfin's TV metadata to default (no per-year separation) so all three collapse to `SeriesName = "Doctor Who"`.
- Set watched state on 5 items, favorites on 3, ratings on 2 (via Jellyfin API before the run).
- Run scanner + fixer end-to-end.
- **Assertions**:
  - Every file at canonical path.
  - Userdata preserved: watched flag on the 5 → still true; favorites on the 3 → still true; ratings on the 2 → still equal.
  - Post-run library validation completes; dashboard reflects new paths.
  - Zero duplicate items in the Jellyfin DB.
  - **Reboot preservation**: after the fixer runs, each of the three Doctor Who reboot folders still exists as a distinct series folder (`Doctor Who (1963)/`, `Doctor Who (2005)/`, `Doctor Who (2024)/`). No episode from one reboot has been merged into another. This is the regression check for issue #43.
  - Restore-from-History reverses every move cleanly (loop: organise → assert new paths → restore → assert original paths, userdata intact throughout).

**Cross-volume test**:

- Fixture library on volume A, target on volume B (via junction or second drive if available on the dev box).
- Assert: staging file (`.mediadash.tmp.{guid}`) cleaned up after success.
- Kill process mid-move (SIGKILL / `taskkill /f`) — verify next fix run's orphan sweep cleans the staging file.

**Chaos test**:

- Kill Jellyfin mid-run at each of steps 8, 9, 11.
- Restart. Verify: no partial state (either fully moved + fully updated in DB, or nothing changed). Any inconsistency = test failure.

### 9.4 Ship gate

The release commit that flips `MediaOrganiser` from experimental to shipped cannot be made until:

1. All three test layers green in CI.
2. A manual smoke test run against the user's actual library on the dev machine, with the user watching. Rename plan reviewed by user before Fix Now clicked. Post-run userdata verified by user in Jellyfin UI (watched/favorite/rating state intact).
3. User signs off explicitly: "ship it".

No premature "it works" claim before all three.

---

## 10. Open questions

None currently. The user has asked to work autonomously — decisions defaulted per Section 3–7.

Sub-questions expected to surface during implementation (not blocking spec):

- Exact `ItemUpdateType` flag to pass to `UpdateItemAsync` — may need `ImageUpdate` too if artwork moves.
- How `ILibraryManager.ValidateMediaLibrary` interacts with a scan already scheduled — cancel-cascade behaviour to be observed on dev server.
- Whether `RelocateIssuePaths` correctly repoints queued Playability / SubtitleFonts issues on the same file — already tested in the existing WIP but worth an integration check.

---

## 11. Non-goals reiterated

To keep future scope-creep pressure honest:

- **No source→library flow.** Downloads folders are out.
- **No custom filename templates.** Jellyfin canonical only.
- **No TMDB / TVDB / AniDB / Shoko direct HTTP calls.** Jellyfin (and its AniDB / Shokofin plugins) are the source of truth; MediaDash reads the provider IDs those plugins populate on `item.ProviderIds` but never queries the services itself (§2, §5.3.3).
- **No music / audiobook / book / comic / picture reorganisation** in this ship.
- **No cross-library moves** (movie flagged as TV — user resolves manually via Errors tab).
