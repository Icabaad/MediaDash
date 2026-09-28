# Format Compatibility Conversion — Design Spec

**Date:** 2026-09-28
**Status:** Draft, awaiting user review
**Feature:** Detect files in formats Jellyfin can't render in-browser, convert them to renderable formats safely.
**Phased rollout:** Phase 1 = format matrix + test harness only. Phases 2–5 = per-kind converters, config, UI, wizard.

---

## 1. Problem

Jellyfin's server indexes a wide superset of extensions (see `Emby.Naming.Common.NamingOptions`), but its browser clients render a much narrower set:

- **Ebooks:** only EPUB and PDF render in the browser. MOBI / AZW / AZW3 / DjVu are indexed but download-only.
- **Comics:** only CBZ reliably renders. CBR and CB7 don't.
- **Audiobooks:** M4B and MP3 play. AAX / AA are DRM-locked; MP3 lacks chapter support.
- **Images:** HEIC / HEIF / TIFF / RAW families are indexed, not shown.
- **Music:** obscure tracker formats (`.mod`, `.it`, `.s3m`, ...) and legacy lossless (`.ape`, `.wv`) are indexed, not played by every client.
- **Video:** already partially covered by `PlayabilityScanner`, extended here only for the format-mismatch tail (e.g. `.rmvb` → `.mkv`).

There is a second, smaller gap: extensions not in `NamingOptions` at all (e.g. `.fb2`, `.lit`). Jellyfin never sees them. A format-conversion pass that also fixes the extension resolves both cases in one action.

Jellyfin itself does not solve this. The user's library owns the files; MediaDash is the housekeeping layer that keeps them playable.

## 2. Scope

**In scope:**

- Detect files whose extension is either not indexed by Jellyfin, or indexed but not renderable by Jellyfin's browser client.
- Convert them to a renderable target format, gated by library-collection-type rules.
- Reuse existing MediaDash infrastructure (`VerifiedReplacementService`, `RecycleBin`, `LibraryGuard`, `FreeSpaceGuard`, `OutputVerifier`, `ProtectionService`, `IssueStore`, `FixTask` ordering).
- Ship the toolchain lazily — first fix pulls Calibre portable on demand into the plugin data folder.
- Ship a real test harness before the first converter: a committed fixture set plus xUnit tests that verify matrix consistency and round-trip conversion.

**Out of scope:**

- **DRM removal.** AAX, Adobe ADEPT EPUB, and encrypted PDFs are detected and flagged with a friendly advisory. Never bypassed. Matches the position the `drm:` prefix already takes in `BookProbeService`.
- **Media acquisition or metadata authoring** (per `PLAN.md` non-goals).
- **Codec-level video repair.** Already owned by `PlayabilityScanner` → `PlayabilityFixer` repair ladder. This spec only handles video *container* mismatches and delegates the codec case to the existing path.

## 3. Architecture

```
Jellyfin.Plugin.MediaDash/
├── Scanners/
│   ├── FormatCompatibilityScanner.cs    # new
│   └── FormatMatrix.cs                  # new — single source of truth
├── Fixers/
│   ├── FormatCompatibilityFixer.cs      # new — dispatches per-kind
│   └── Converters/
│       ├── IFormatConverter.cs          # new
│       ├── CalibreToolchain.cs          # new — lazy-download orchestrator
│       ├── EbookConverter.cs            # Phase 2
│       ├── ComicConverter.cs            # Phase 2 (SharpCompress; no Calibre needed)
│       ├── AudiobookConverter.cs        # Phase 3
│       ├── ImageConverter.cs            # Phase 4
│       ├── MusicConverter.cs            # Phase 4
│       └── VideoConverter.cs            # Phase 4
└── (existing Tests project extended — see §7)
```

**Data flow (scan):**

```
FormatCompatibilityScanner.RunAsync
  → for each converter registered in the DI container:
      → for each library folder whose CollectionType ∈ converter.AllowedLibraryTypes:
          → walk items under it
          → for each file:
              row = FormatMatrix.Lookup(ext)
              if row is null OR row.ClientRenders: continue
              if converter.CanConvert(ext):
                  emit Issue(type=FormatCompatibility,
                             details={sourceExt, targetExt, converterKey, libraryKind},
                             suggestedFix=row.UserAdvice)
```

**Data flow (fix), inside `FormatCompatibilityFixer.FixAsync(issue)`:**

1. Re-resolve `BaseItem` by `IssueId`. Gone → `FixReason.SourceMissing`.
2. Resolve parent `CollectionFolder.CollectionType`.
3. If `null` or `MixedContent` → refuse with `FixReason.MixedLibraryUserReviewOnly` unless the issue was manually approved (per-row Approve).
4. If `CollectionType ∉ converter.AllowedLibraryTypes` → refuse with `FixReason.LibraryTypeMismatch`.
5. Run existing guards: `LibraryGuard.IsWritable`, `ProtectionService.IsProtected`, `FreeSpaceGuard.HasHeadroom`, config-provider snapshot recheck for late `FixMode.DetectOnly` flip.
6. `converter.EnsureToolchainAsync(ct)` — may trigger `CalibreToolchain.EnsureAsync` (first-fix download).
7. `converter.ConvertAsync(src, staging, progress, ct)` where `staging = src + TranscodeFixer.InflightTail + targetExt`.
8. `VerifiedReplacementService.CommitAsync(src, staging, RecycleOriginal: true, disposal)` — the shared verify → promote → dispose → commit protocol.
9. `ILibraryMonitor.ReportFileSystemChanged` + `ILibraryManager` row purge (matches `PlayabilityFixer` pattern).

**Reused services (no changes required):** `FfmpegExecutor`, `FfprobeService`, `BookProbeService`, `ComicProbeService`, `VerifiedReplacementService`, `RecycleBin`, `OutputVerifier`, `LibraryGuard`, `FreeSpaceGuard`, `ProtectionService`, `IPluginConfigurationProvider`, `IssueStore`, `FixTask` ordering, `AnalyticsReporter` counters.

## 4. Format Matrix

Single machine-readable table in `Scanners/FormatMatrix.cs`. Each row:

```csharp
public sealed record FormatEntry(
    string Extension,
    MediaKind Kind,
    bool ServerIndexes,       // in Jellyfin's NamingOptions?
    bool ClientRenders,       // does the browser player/reader handle it?
    string? TargetExtension,  // null → detect-only, no conversion path
    string ConverterKey,      // "ebook" | "comic" | "audiobook" | "image" | "music" | "video" | "none"
    string UserAdvice);
```

**Initial matrix (Phase 1 deliverable):**

| Kind | Ext | Indexed | Renders | Target | Converter | Notes |
|---|---|---|---|---|---|---|
| Ebook | `.epub` | Y | Y | — | none | native |
| Ebook | `.pdf` | Y | Y | — | none | native; password-protected → DRM advisory |
| Ebook | `.mobi` | Y | N | `.epub` | ebook | Calibre |
| Ebook | `.azw` | Y | N | `.epub` | ebook | Calibre |
| Ebook | `.azw3` | Y | N | `.epub` | ebook | Calibre |
| Ebook | `.djvu` | Y | N | `.pdf` | ebook | Calibre |
| Ebook | `.txt` | N | N | `.epub` | ebook | Calibre; also fixes indexing |
| Ebook | `.rtf` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.doc` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.docx` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.fb2` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.lit` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.lrf` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.pdb` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.htmlz` | N | N | `.epub` | ebook | Calibre |
| Ebook | `.chm` | N | N | `.epub` | ebook | Calibre |
| Comic | `.cbz` | Y | Y | — | none | native |
| Comic | `.cbr` | Y | partial | `.cbz` | comic | SharpCompress repack |
| Comic | `.cb7` | Y | N | `.cbz` | comic | SharpCompress repack |
| Audiobook | `.m4b` | Y | Y | — | none | native (chaptered AAC) |
| Audiobook | `.mp3` | Y | Y | — | none | native, but folder-of-mp3s → `.m4b` merge (opt-in) |
| Audiobook | `.aax` | Y | N | — | none | DRM advisory only |
| Audiobook | `.aa` | Y | N | — | none | DRM advisory only |
| Image | `.heic` | Y | N | `.jpg` | image | ffmpeg |
| Image | `.heif` | Y | N | `.jpg` | image | ffmpeg |
| Image | `.tif` | Y | N | `.jpg` | image | ffmpeg |
| Image | `.tiff` | Y | N | `.jpg` | image | ffmpeg |
| Image | `.bmp` | Y | N | `.jpg` | image | ffmpeg |
| Image | `.raw` | Y | N | `.jpg` | image | ffmpeg via libraw |
| Image | `.nef` | Y | N | `.jpg` | image | ffmpeg via libraw |
| Image | `.cr2` | Y | N | `.jpg` | image | ffmpeg via libraw |
| Image | `.arw` | Y | N | `.jpg` | image | ffmpeg via libraw |
| Image | `.dng` | Y | N | `.jpg` | image | ffmpeg via libraw |
| Music | `.669` | Y | N | `.flac` | music | ffmpeg (tracker) |
| Music | `.it` | Y | N | `.flac` | music | ffmpeg (tracker) |
| Music | `.mod` | Y | N | `.flac` | music | ffmpeg (tracker) |
| Music | `.s3m` | Y | N | `.flac` | music | ffmpeg (tracker) |
| Music | `.xm` | Y | N | `.flac` | music | ffmpeg (tracker) |
| Music | `.ape` | Y | partial | `.flac` | music | ffmpeg — losslessly repack |
| Music | `.wv` | Y | partial | `.flac` | music | ffmpeg |
| Music | `.tak` | Y | partial | `.flac` | music | ffmpeg |
| Music | `.tta` | Y | partial | `.flac` | music | ffmpeg |
| Music | `.wma` | Y | partial | `.mp3` | music | ffmpeg |
| Video | `.rmvb` | Y | N | `.mkv` | video | ffmpeg |
| Video | (codec-level issues) | Y | varies | — | none | delegated to existing `PlayabilityScanner`/`PlayabilityFixer` |

**Multi-file audiobook folder** — modelled as a synthetic `MediaKind.AudiobookFolder` row with no extension. Detected when a folder contains ≥3 sequentially-numbered `.mp3` or `.m4a` files with no `.m4b` sibling, inside a Books library. Behind opt-in `AudiobookMergeMultiFile` config toggle.

The matrix is data-only. `Classify(path)` is a one-line lookup helper. No business logic.

## 5. Library-Kind Gate

Enforced at **both** scan-emit time and fix-time. Two-point enforcement handles the "user retagged the library between scan and fix" case.

Each converter declares its permitted `CollectionType?` set:

| Converter | Allowed CollectionTypes |
|---|---|
| Ebook | `Books` |
| Audiobook | `Books` |
| Comic | `Books` |
| Image | `HomeVideos` |
| Music | `Music` |
| Video | `Movies`, `TvShows` |

**Mixed library rule (locked):** any library with `CollectionType = null` or `CollectionType.MixedContent` is user-review-only, regardless of converter. `FormatCompatibilityScanner` still emits Issues (silence would be worse UX), but `FormatCompatibilityFixer` refuses to run under both `FixMode.Automatic` and `FixMode.OnApproval`. The only way to fix a Mixed-library file is the per-row Approve button in the Issues tab, which prompts:

> "This file lives in a mixed-content library. MediaDash can't verify the file's kind from the library type alone. Convert anyway?"

The mixed rule is checked *before* the `AllowedLibraryTypes` gate — no converter can bypass it by declaring `MixedContent` allowed. On per-row approve in a mixed library, the converter's `CanConvert(ext)` still runs to pick which converter to invoke; the library gate is the only check that's waived.

Rejection reasons surface as `FixResult.Fail` with new `FixReason` values:

- `LibraryTypeMismatch` — converter's `AllowedLibraryTypes` doesn't include this library's `CollectionType`.
- `LibraryTypeUnknown` — `CollectionType` couldn't be resolved.
- `MixedLibraryUserReviewOnly` — Mixed library, no per-row approve on record.

## 6. Toolchain: `CalibreToolchain`

The one bespoke piece. Everything else reuses existing services.

**Contract:**

```csharp
public sealed class CalibreToolchain
{
    public Task<CalibrePaths?> EnsureAsync(CancellationToken ct);
    public bool IsAvailable { get; }
    public string? EbookConvertPath { get; }
}
```

**Behaviour:**

1. On construction, checks:
   - `CalibreOverridePath` config value — if set and `ebook-convert` exists there, cache and return.
   - System `PATH` — if `ebook-convert` (or `ebook-convert.exe`) is discoverable, cache and return.
   - `%pluginData%/tools/calibre-<version>-<platform>/` — if extracted and version-file matches, cache and return.
2. First call to `EnsureAsync` when none of the above resolve triggers download:
   - Determine platform: `RuntimeInformation.OSDescription` + `Architecture`. Supported: Windows-x64, Linux-x64, Linux-arm64, macOS-x64, macOS-arm64.
   - Download URL is a compile-time constant per platform, pointing at Calibre's official portable archive.
   - SHA-256 checksum is baked into `CalibreToolchain.cs` per platform. Mismatch → abort with clear error.
   - Extract to `%pluginData%/tools/calibre-<version>-<platform>/` using `SharpCompress` (already a runtime sidecar).
   - Write a `.version` marker file so subsequent runs skip the download.
   - Progress reported via `IProgress<double>` all the way up to `FixTask`'s task-status feed.
3. Download failures return a null result. `FormatCompatibilityFixer` maps null to `FixReason.ConverterUnavailable` with a message pointing at `CalibreOverridePath`.

**Why lazy-download, not bundled:** the plugin zip stays ~2 MB. Users who never convert ebooks never pay the 150 MB cost. Users who do pay it once. Matches how MediaDash already leans on the host-provided ffmpeg / ffprobe.

**License compliance:** Calibre is GPLv3. MediaDash is already GPLv3. `CalibreToolchain.cs` XML docs cite Calibre's source URL and the redistribution route (we don't ship the binary; we download it from the upstream author on user's own machine). No plugin-zip change required; add a one-liner to `README.md` and `CHANGELOG.md` on the Phase 2 release.

## 7. Test Harness (Phase 1 deliverable)

Extends the existing `Jellyfin.Plugin.MediaDash.Tests` xUnit project. No new test frameworks, no fixtures generation code — real committed files.

**Layout:**

```
Jellyfin.Plugin.MediaDash.Tests/
├── Formats/
│   ├── FormatMatrixConsistencyTests.cs      # invariants: no orphan rows, every converter key resolves
│   ├── FormatMatrixCoverageTests.cs         # every unrenderable row has a fixture + converter
│   ├── FormatFixtureBase.cs                 # helpers: temp dir, invoke converter, probe output
│   ├── EbookConverterTests.cs               # [Theory] per matrix row (skipped until Phase 2)
│   ├── ComicConverterTests.cs
│   ├── AudiobookConverterTests.cs
│   ├── ImageConverterTests.cs
│   ├── MusicConverterTests.cs
│   ├── VideoConverterTests.cs
│   ├── LibraryScopeGateTests.cs             # collection-type refusal invariants
│   ├── MixedLibraryPolicyTests.cs           # mixed → detect-only invariant
│   ├── CalibreToolchainMockTests.cs         # default CI: swaps downloader for file:// stub
│   └── CalibreToolchainDownloadTests.cs     # [Trait("Category","LiveDownload")] — weekly CI only
└── fixtures/formats/
    ├── ebook/  audiobook/  comic/  image/  music/  video/
    └── README.md                            # per-file provenance + CC0/PD guarantee
```

**Fixture sourcing rules (`fixtures/formats/README.md`):**

- Every fixture is CC0, public-domain, or self-created. Provenance URL noted per file.
- Every fixture < 200 KB. Larger required-size formats commit a stub and tag `[Trait("Category","LargeFixture")]` (skipped in default runs).

**Assertion strategy** — reuse the plugin's own probes so tests exercise the exact code paths production runs:

```csharp
protected async Task AssertConvertsAndProbesCleanAsync(
    IFormatConverter converter, string fixturePath, string targetExt)
{
    using var tmp = new TempWorkDir();
    var staging = Path.Combine(tmp.Path, "out" + targetExt);
    var result = await converter.ConvertAsync(fixturePath, staging, null, CancellationToken.None);
    Assert.True(result.Success, result.Reason);
    Assert.True(File.Exists(staging));
    switch (targetExt)
    {
        case ".epub" or ".pdf": Assert.True(BookProbeService.Probe(staging).Ok); break;
        case ".cbz":            Assert.True(ComicProbeService.Probe(staging).Ok); break;
        case ".jpg":            using (var img = SkiaSharp.SKBitmap.Decode(staging)) Assert.NotNull(img); break;
        case ".m4b" or ".flac" or ".mp3" or ".mkv":
            var probe = await _ffprobe.ProbeAsync(staging, CancellationToken.None);
            Assert.NotNull(probe);
            Assert.True(probe!.Streams.Any());
            break;
    }
}
```

**Matrix coverage test** — the safety net that fails CI when a row is added without its fixture / converter:

```csharp
[Fact]
public void EveryUnrenderableRowHasFixtureAndConverter()
{
    var missing = new List<string>();
    foreach (var row in FormatMatrix.All.Where(r => !r.ClientRenders && r.TargetExtension is not null))
    {
        var kind = row.Kind.ToString().ToLowerInvariant();
        var fixture = Path.Combine(FixturesRoot, kind, "sample" + row.Extension);
        if (!File.Exists(fixture))               missing.Add($"fixture: {fixture}");
        if (!_registry.HasConverter(row.ConverterKey)) missing.Add($"converter: {row.ConverterKey}");
    }
    Assert.Empty(missing);
}
```

**What Phase 1 exits with:**

1. `FormatMatrix.cs` committed with the full audit above.
2. `fixtures/formats/` populated with tiny real files for every row that has a `TargetExtension`.
3. `FormatMatrixConsistencyTests` + `FormatMatrixCoverageTests` + `CalibreToolchainMockTests` all green.
4. Per-converter test classes exist with `[Theory]` rows filled in and `Skip="Converter not implemented until Phase N"` set. Removing the skip in later phases is the only edit needed.
5. `IssueType.FormatCompatibility` in the enum + minimal Issues-tab rendering wired so scan output shows up.

## 8. Configuration

Appended to `PluginConfiguration`. All defaults chosen so the feature ships silent on upgrade — nothing converts until the user opts in.

```csharp
public FixMode FormatCompatibilityFixMode { get; set; } = FixMode.DetectOnly;
public DisposalMethod FormatCompatibilityDisposal { get; set; } = DisposalMethod.RecycleBin;

public bool ConvertEbooks { get; set; }
public bool ConvertAudiobooks { get; set; }
public bool ConvertComics { get; set; }
public bool ConvertImages { get; set; }
public bool ConvertMusic { get; set; }
public bool ConvertVideos { get; set; }

public bool AudiobookMergeMultiFile { get; set; }

public string CalibreOverridePath { get; set; } = string.Empty;

public string EbookPreferredTarget { get; set; } = string.Empty;   // "epub" | "pdf"
public string ImagePreferredTarget { get; set; } = string.Empty;   // "jpg" | "png" | "webp"
public string MusicPreferredTarget { get; set; } = string.Empty;   // "flac" | "mp3"
```

Wire the new `IssueType.FormatCompatibility` into `GetFixMode` and `GetDisposal` switch expressions.

## 9. UI Surfaces

Three edits to existing HTML/JS — no new UI files.

1. **Settings tab → new section "Format conversion"** in `Configuration/scripts/tabs/settings-tab.js`. One master fix-mode dropdown, one disposal dropdown, six per-kind toggles, three target-format dropdowns, one path field for `CalibreOverridePath`, one "Test Calibre" button that runs `CalibreToolchain.EnsureAsync` and reports back.
2. **Overview tab → new card "Unsupported formats"** showing counts by kind (`12 ebooks · 3 audiobooks · 47 comics`). Click routes to a pre-filtered Issues tab (`?type=FormatCompatibility`).
3. **First-run wizard → new step "Format conversion"** — one screen: "Some of your files may be in formats Jellyfin can't display in the browser. MediaDash can convert them safely." Radio: `Detect only (recommended)` / `Automatic per-kind` / `Skip`.

**Download progress UX:** first fix that needs Calibre triggers a synchronous download inside `FixTask`. Overview banner: `Downloading ebook converter (150 MB)... 42%`. Progress uses the existing `IProgress<double>` chain — no new channel.

## 10. Phased Rollout

Each phase ships as its own tagged release. Phase-per-release cadence keeps the blast radius small and lets us gate advancement on real user feedback rather than internal progress reports.

| Phase | Version | Scope |
|---|---|---|
| **1** | `1.0.9.0` (roadmap-slotted) | `FormatMatrix`, `FormatCompatibilityScanner` (scan-only), `CalibreToolchain` (mock-tested only), test harness, fixtures, `IssueType.FormatCompatibility`, minimal Issues-tab rendering. User can scan and see what's flagged. No conversion runs. |
| **2** | `1.1.0.0` | `EbookConverter` + `ComicConverter`. Real `CalibreToolchain` download flow. Settings section + Overview card. |
| **3** | `1.2.0.0` | `AudiobookConverter` with DRM refusal + `AudiobookMergeMultiFile` opt-in. |
| **4** | `1.3.0.0` | `ImageConverter`, `MusicConverter`, `VideoConverter`. |
| **5** | `1.3.x.y` | First-run wizard step + analytics counters + release announcement. |

**Shipping rules that stay locked across every phase** (from repo memory):
- Bugfix-only phases bump the 4th digit; user approval required to waive.
- No `git push` or `gh release create` without explicit per-push approval.
- Full pre-push E2E sweep + CHANGELOG reconciliation before any live push.
- CHANGELOG bullet added the moment any user-visible change lands.
- Manifest.json ships two entries per release (v10 ABI + v12 ABI).
- All 8 runtime files (main DLL + 6 sidecars + deps.json) go into every release zip.

## 11. Error Handling

New `FixReason` values (extend the existing enum):

- `LibraryTypeMismatch` — parent CollectionType outside converter's allowed set.
- `LibraryTypeUnknown` — CollectionType couldn't be resolved.
- `MixedLibraryUserReviewOnly` — Mixed library, no per-row approve on record.
- `ConverterUnavailable` — toolchain missing and lazy-download failed.
- `ConverterFailed` — the converter ran but its output failed the round-trip probe check.
- `SourceExtensionNotConvertible` — matrix has no target for this extension (shouldn't happen at fix time; safety net).

Every converter failure at any point in the fix flow routes through `VerifiedReplacementService` so the original file is never touched. `RecycleBin` retains the original on successful commits; on failure, the staging file is discarded.

## 12. Analytics

Add per-converter counters to `AnalyticsReporter`'s aggregated per-run report — matches existing per-scanner reporting. Counted metrics:

- Detected count per converter key.
- Fixed count per converter key.
- Bytes reclaimed (source size − target size; often negative for EPUB conversions where the target is larger).
- First-download event: platform + Calibre version (no other identifying info).

No file paths, no filenames, no user identifiers. Same anti-fingerprinting posture the plugin already uses.

## 13. Open Questions

None at spec sign-off. Open questions surface as `WriteupPending` in the writing-plans skill output.

