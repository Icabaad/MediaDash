# Format Compatibility Conversion — Phase 1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the format matrix, a scan-only `FormatCompatibilityScanner`, a mock-testable `CalibreToolchain` scaffold, a committed fixture library, and the xUnit safety-net tests that lock the deliverable in — so Phase 2 can add converters without re-litigating any of the above.

**Architecture:** One central `FormatMatrix` data table drives scan-time detection. `FormatCompatibilityScanner` walks items per allowed `CollectionType` per converter and emits `Issue(type=FormatCompatibility)` rows. Converters register via an `IFormatConverter` interface; no converter implementations ship in Phase 1 (scan-only). `CalibreToolchain` ships as a scaffold + `IToolchainDownloader` abstraction whose downloader is swapped in tests; no real download URL or checksum yet.

**Tech Stack:** C# net9.0, xUnit 2.9, Jellyfin 10.11/12 dual-target, Microsoft.Extensions.DependencyInjection, existing MediaDash conventions (`ProbingScannerBase`, `IssueStore`, `PluginServiceRegistrator`).

**Reference spec:** [`docs/superpowers/specs/2026-09-28-format-compatibility-conversion-design.md`](../specs/2026-09-28-format-compatibility-conversion-design.md)

---

## File Structure

**Created:**
- `Jellyfin.Plugin.MediaDash/Scanners/FormatMatrix.cs` — data table + `MediaKind`, `FormatEntry`, `Classify(path)`.
- `Jellyfin.Plugin.MediaDash/Scanners/FormatCompatibilityScanner.cs` — `IScanner` implementation.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/IFormatConverter.cs` — converter contract.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/IFormatConverterRegistry.cs` — DI-fed converter lookup by key.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/FormatConverterRegistry.cs` — the trivial `IEnumerable<IFormatConverter>` → dictionary implementation.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/CalibreToolchain.cs` — scaffold + `IToolchainDownloader` abstraction.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/IToolchainDownloader.cs` — downloader contract (mockable).
- `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatMatrixConsistencyTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatMatrixCoverageTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityScannerTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreToolchainMockTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatFixtureBase.cs` — shared helpers.
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/README.md` — provenance table.
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/ebook/*.{mobi,azw3,djvu,txt,fb2,rtf,doc,docx,lit,lrf,pdb,htmlz,chm}` — tiny real files.
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/comic/*.{cbr,cb7}`
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/audiobook/sample.aax` — DRM fixture (asserts refusal, not conversion).
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/image/*.{heic,heif,tif,tiff,bmp,raw,nef,cr2,arw,dng}`
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/music/*.{669,it,mod,s3m,xm,ape,wv,tak,tta,wma}`
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/video/sample.rmvb`

**Modified:**
- `Jellyfin.Plugin.MediaDash/Data/IssueType.cs` — add `FormatCompatibility = 19`.
- `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs` — register `FormatCompatibilityScanner` (as `IScanner`), `FormatConverterRegistry` (as `IFormatConverterRegistry`), `CalibreToolchain`.
- `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/issues-tab.js` — label mapping for the new `IssueType`.
- `CHANGELOG.md` — one bullet under the current in-progress version.

---

## Task 1: Add `IssueType.FormatCompatibility` enum value

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Data/IssueType.cs`

- [ ] **Step 1: Add the enum value**

Append after the last existing entry (`ManualDelete = 18`):

```csharp
    /// <summary>A file whose format Jellyfin's browser client cannot render (e.g. MOBI ebook, CBR comic, HEIC image). The scanner suggests conversion to a renderable target format; the fixer runs it in Phase 2+.</summary>
    FormatCompatibility = 19
}
```

- [ ] **Step 2: Build to confirm no compile break**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: build succeeds. Any `switch` expression missing this case would produce CS8509 warnings — none appear because the existing `switch` expressions in `PluginConfiguration.GetFixMode`/`GetDisposal` use a `_ => default` arm.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Data/IssueType.cs
git commit -m "feat(data): add IssueType.FormatCompatibility (Phase 1 of format-conversion feature)"
```

---

## Task 2: Write `FormatMatrixConsistencyTests` (failing)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatMatrixConsistencyTests.cs`

- [ ] **Step 1: Create the test file**

```csharp
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatMatrixConsistencyTests
{
    [Fact]
    public void AllRowsHaveNonEmptyExtension()
    {
        Assert.All(FormatMatrix.All, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Extension), $"row for {row.Kind} has empty extension");
            Assert.StartsWith(".", row.Extension);
            Assert.Equal(row.Extension.ToLowerInvariant(), row.Extension);
        });
    }

    [Fact]
    public void NoDuplicateExtensions()
    {
        var dupes = FormatMatrix.All.GroupBy(r => r.Extension).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void RenderableRowsHaveNoTarget()
    {
        Assert.All(
            FormatMatrix.All.Where(r => r.ClientRenders),
            row => Assert.Null(row.TargetExtension));
    }

    [Fact]
    public void RenderableRowsHaveConverterKeyNone()
    {
        Assert.All(
            FormatMatrix.All.Where(r => r.ClientRenders),
            row => Assert.Equal("none", row.ConverterKey));
    }

    [Fact]
    public void UnrenderableRowsWithTargetHaveConverterKey()
    {
        Assert.All(
            FormatMatrix.All.Where(r => !r.ClientRenders && r.TargetExtension is not null),
            row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(row.ConverterKey));
                Assert.NotEqual("none", row.ConverterKey);
            });
    }

    [Fact]
    public void ClassifyKnownExtension()
    {
        var row = FormatMatrix.Classify("/some/path/book.mobi");
        Assert.NotNull(row);
        Assert.Equal(MediaKind.Ebook, row!.Kind);
        Assert.Equal(".mobi", row.Extension);
    }

    [Fact]
    public void ClassifyUnknownExtension()
    {
        Assert.Null(FormatMatrix.Classify("/some/path/whatever.xyz"));
    }

    [Fact]
    public void ClassifyIsCaseInsensitive()
    {
        Assert.NotNull(FormatMatrix.Classify("/BOOK.MOBI"));
        Assert.NotNull(FormatMatrix.Classify("/book.MoBi"));
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail with missing type**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatMatrixConsistencyTests --nologo`
Expected: build fails with `CS0246: The type or namespace name 'FormatMatrix' could not be found`.

---

## Task 3: Create `FormatMatrix.cs`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Scanners/FormatMatrix.cs`

- [ ] **Step 1: Write the file**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// The kind of media a file is, for the format-compatibility feature. Different from Jellyfin's
/// BaseItemKind because it's coarser (Ebook vs. Comic is one distinction Jellyfin doesn't draw)
/// and includes synthetic entries (AudiobookFolder — a folder of chaptered mp3s) that Phase 3 adds.
/// </summary>
public enum MediaKind
{
    Video,
    Audio,
    Audiobook,
    Ebook,
    Comic,
    Image,
    Music
}

/// <summary>
/// One row in the format-compatibility matrix. See
/// docs/superpowers/specs/2026-09-28-format-compatibility-conversion-design.md §4.
/// </summary>
/// <param name="Extension">Lower-case extension including the dot (e.g. ".mobi").</param>
/// <param name="Kind">Media kind this extension classifies as.</param>
/// <param name="ServerIndexes">True when Jellyfin's NamingOptions include this extension.</param>
/// <param name="ClientRenders">True when Jellyfin's browser client can display the file.</param>
/// <param name="TargetExtension">Extension a converter should produce, or null to detect-only.</param>
/// <param name="ConverterKey">Registry key of the converter that handles this row; "none" when no conversion.</param>
/// <param name="UserAdvice">One-line SuggestedFix text shown on the Issues tab.</param>
public sealed record FormatEntry(
    string Extension,
    MediaKind Kind,
    bool ServerIndexes,
    bool ClientRenders,
    string? TargetExtension,
    string ConverterKey,
    string UserAdvice);

/// <summary>
/// Single source of truth for which extensions Jellyfin can render, and what to convert them to
/// when it can't. Read by <see cref="FormatCompatibilityScanner"/> and by the Phase 2+ converters.
/// Data only — the only code here is the ext-to-row lookup.
/// </summary>
public static class FormatMatrix
{
    /// <summary>Every row in the matrix. Order irrelevant.</summary>
    public static readonly IReadOnlyList<FormatEntry> All = new FormatEntry[]
    {
        // Ebooks — Jellyfin's browser reader handles EPUB + PDF only.
        new(".epub", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: true,  TargetExtension: null,   ConverterKey: "none",  UserAdvice: ""),
        new(".pdf",  MediaKind.Ebook, ServerIndexes: true,  ClientRenders: true,  TargetExtension: null,   ConverterKey: "none",  UserAdvice: ""),
        new(".mobi", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert MOBI to EPUB so Jellyfin's browser reader can display it."),
        new(".azw",  MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert AZW to EPUB so Jellyfin's browser reader can display it."),
        new(".azw3", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert AZW3 to EPUB so Jellyfin's browser reader can display it."),
        new(".djvu", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".pdf",  ConverterKey: "ebook", UserAdvice: "Convert DjVu to PDF so Jellyfin's browser reader can display it."),
        new(".txt",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert plain text to EPUB so Jellyfin indexes and displays it."),
        new(".rtf",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert RTF to EPUB so Jellyfin indexes and displays it."),
        new(".doc",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert DOC to EPUB so Jellyfin indexes and displays it."),
        new(".docx", MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert DOCX to EPUB so Jellyfin indexes and displays it."),
        new(".fb2",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert FB2 to EPUB so Jellyfin indexes and displays it."),
        new(".lit",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert LIT to EPUB so Jellyfin indexes and displays it."),
        new(".lrf",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert LRF to EPUB so Jellyfin indexes and displays it."),
        new(".pdb",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert PDB to EPUB so Jellyfin indexes and displays it."),
        new(".htmlz",MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert HTMLZ to EPUB so Jellyfin indexes and displays it."),
        new(".chm",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert CHM to EPUB so Jellyfin indexes and displays it."),

        // Comics.
        new(".cbz",  MediaKind.Comic, ServerIndexes: true,  ClientRenders: true,  TargetExtension: null,   ConverterKey: "none",  UserAdvice: ""),
        new(".cbr",  MediaKind.Comic, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".cbz", ConverterKey: "comic", UserAdvice: "Repack CBR as CBZ so every Jellyfin client can display it."),
        new(".cb7",  MediaKind.Comic, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".cbz", ConverterKey: "comic", UserAdvice: "Repack CB7 as CBZ so every Jellyfin client can display it."),

        // Audiobooks — DRM formats detected but never converted.
        new(".m4b",  MediaKind.Audiobook, ServerIndexes: true, ClientRenders: true,  TargetExtension: null, ConverterKey: "none", UserAdvice: ""),
        new(".aax",  MediaKind.Audiobook, ServerIndexes: true, ClientRenders: false, TargetExtension: null, ConverterKey: "none", UserAdvice: "AAX is Audible-DRM encrypted. Convert it with Audible or a DRM-removal tool of your choice, then re-import the M4B."),
        new(".aa",   MediaKind.Audiobook, ServerIndexes: true, ClientRenders: false, TargetExtension: null, ConverterKey: "none", UserAdvice: "AA is Audible-DRM encrypted. Convert it with Audible or a DRM-removal tool of your choice, then re-import the M4B."),

        // Images — the browser client renders JPG/PNG/GIF/WebP but not HEIC/TIFF/BMP/RAW.
        new(".heic", MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert HEIC to JPG for browser display."),
        new(".heif", MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert HEIF to JPG for browser display."),
        new(".tif",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert TIFF to JPG for browser display."),
        new(".tiff", MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert TIFF to JPG for browser display."),
        new(".bmp",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert BMP to JPG for browser display."),
        new(".raw",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".nef",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert Nikon RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".cr2",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert Canon RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".arw",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert Sony RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".dng",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert DNG to JPG for browser display (original preserved in recycle bin)."),

        // Music — obscure tracker formats and legacy lossless.
        new(".669",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".it",   MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".mod",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".s3m",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".xm",   MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".ape",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack Monkey's Audio as FLAC for broader client support (lossless)."),
        new(".wv",   MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack WavPack as FLAC for broader client support (lossless)."),
        new(".tak",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack TAK as FLAC for broader client support (lossless)."),
        new(".tta",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack TTA as FLAC for broader client support (lossless)."),
        new(".wma",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".mp3",  ConverterKey: "music", UserAdvice: "Convert WMA to MP3 for broader client support."),

        // Video — one entry. Codec-level video problems are handled by PlayabilityScanner.
        new(".rmvb", MediaKind.Video, ServerIndexes: true, ClientRenders: false, TargetExtension: ".mkv", ConverterKey: "video", UserAdvice: "Repackage RMVB into MKV so Jellyfin's transcoder can handle it."),
    };

    private static readonly IReadOnlyDictionary<string, FormatEntry> ByExt =
        All.ToDictionary(r => r.Extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the row for a file's extension, or null when the extension isn't in the matrix.</summary>
    /// <param name="path">Any string ending in an extension; only the extension is inspected.</param>
    public static FormatEntry? Classify(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        return ByExt.TryGetValue(ext, out var row) ? row : null;
    }
}
```

- [ ] **Step 2: Run consistency tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatMatrixConsistencyTests --nologo`
Expected: all 8 tests pass.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Scanners/FormatMatrix.cs Jellyfin.Plugin.MediaDash.Tests/Formats/FormatMatrixConsistencyTests.cs
git commit -m "feat(scanners): add FormatMatrix data table and consistency tests"
```

---

## Task 4: Add `IFormatConverter` and `IFormatConverterRegistry`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/IFormatConverter.cs`
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/IFormatConverterRegistry.cs`
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/FormatConverterRegistry.cs`
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/ConverterAvailability.cs`

- [ ] **Step 1: Create `ConverterAvailability.cs`**

```csharp
namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>Whether a converter is ready to run.</summary>
public enum ConverterAvailability
{
    /// <summary>Converter and its toolchain are present and usable.</summary>
    Ready,

    /// <summary>Toolchain download or extraction pending — call <see cref="IFormatConverter.EnsureToolchainAsync"/>.</summary>
    NeedsDownload,

    /// <summary>Toolchain fetch failed or is unavailable on this platform — cannot fix, only detect.</summary>
    Unavailable
}
```

- [ ] **Step 2: Create `IFormatConverter.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Scanners;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// A per-media-kind converter. Phase 1 ships no implementations — this is the contract
/// scanners and (Phase 2+) fixers use to route work.
/// </summary>
public interface IFormatConverter
{
    /// <summary>Registry key referenced by <see cref="FormatEntry.ConverterKey"/>.</summary>
    string Key { get; }

    /// <summary>The media kind this converter handles.</summary>
    MediaKind Kind { get; }

    /// <summary>
    /// Collection types under which this converter is allowed to touch files. Enforced at both
    /// scan-emit time and fix-time. See spec §5.
    /// </summary>
    IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; }

    /// <summary>True if the source extension is one this converter knows how to handle.</summary>
    /// <param name="sourceExtension">Lowercased extension including the dot (e.g. ".mobi").</param>
    bool CanConvert(string sourceExtension);

    /// <summary>Whether the converter's toolchain is present. Cheap — no I/O beyond file-exists.</summary>
    ConverterAvailability Availability { get; }

    /// <summary>
    /// Ensures the toolchain is available. May trigger a lazy download the first time.
    /// Phase 1 implementations are permitted to return <see cref="ConverterAvailability.Unavailable"/>
    /// unconditionally; real conversion lands in Phase 2+.
    /// </summary>
    Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken);
}
```

- [ ] **Step 3: Create `IFormatConverterRegistry.cs`**

```csharp
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>Lookup layer over the DI-registered <see cref="IFormatConverter"/> instances.</summary>
public interface IFormatConverterRegistry
{
    /// <summary>Every registered converter.</summary>
    IReadOnlyCollection<IFormatConverter> All { get; }

    /// <summary>Returns the converter for the given key, or null when none is registered.</summary>
    IFormatConverter? Get(string key);
}
```

- [ ] **Step 4: Create `FormatConverterRegistry.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <inheritdoc />
public sealed class FormatConverterRegistry : IFormatConverterRegistry
{
    private readonly Dictionary<string, IFormatConverter> _byKey;

    /// <summary>DI-fed constructor; every IFormatConverter registered with the container lands here.</summary>
    /// <param name="converters">All converters resolved from DI.</param>
    public FormatConverterRegistry(IEnumerable<IFormatConverter> converters)
    {
        var list = converters.ToList();
        All = list;
        _byKey = list.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<IFormatConverter> All { get; }

    /// <inheritdoc />
    public IFormatConverter? Get(string key)
        => _byKey.TryGetValue(key, out var c) ? c : null;
}
```

- [ ] **Step 5: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/Converters/
git commit -m "feat(converters): add IFormatConverter contract and registry (no implementations yet)"
```

---

## Task 5: Write `FormatCompatibilityScannerTests` (failing)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityScannerTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatCompatibilityScannerTests
{
    private static IReadOnlyList<Issue> Run(
        FormatCompatibilityScanner scanner,
        params BaseItem[] items)
        => scanner.ScanAsync(items, new Progress<double>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();

    private static BaseItem MakeItem(string path, CollectionType? libraryType)
        => new FakeBaseItem(path, libraryType);

    private static FormatCompatibilityScanner Scanner(params IFormatConverter[] converters)
        => new(new FormatConverterRegistry(converters), NullLogger<FormatCompatibilityScanner>.Instance);

    [Fact]
    public void EmitsIssueForUnrenderableExtension()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.mobi", CollectionType.Books);
        var issues = Run(scanner, item);
        var issue = Assert.Single(issues);
        Assert.Equal(IssueType.FormatCompatibility, issue.Type);
        Assert.Equal(item.Path, issue.Path);
        Assert.Contains("EPUB", issue.SuggestedFix);
    }

    [Fact]
    public void SkipsRenderableExtensions()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.epub", CollectionType.Books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsWhenLibraryTypeOutsideConverterAllowedSet()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\movies\thing.mobi", CollectionType.Movies);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void EmitsIssueInMixedLibraryButFlagsUserReviewOnly()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\mixed\thing.mobi", CollectionType.MixedContent);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Contains("mixed", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkipsWhenNoConverterKnowsTheExtension()
    {
        var scanner = Scanner(new FakeConverter("music", MediaKind.Music, CollectionType.Music));
        var item = MakeItem(@"C:\books\thing.mobi", CollectionType.Books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsUnknownExtensions()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.xyz", CollectionType.Books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SetsIssueStatusToDetected()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.mobi", CollectionType.Books);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Equal(IssueStatus.Detected, issue.Status);
    }

    private sealed class FakeConverter : IFormatConverter
    {
        public FakeConverter(string key, MediaKind kind, params CollectionType?[] allowed)
        {
            Key = key;
            Kind = kind;
            AllowedLibraryTypes = new HashSet<CollectionType?>(allowed);
        }

        public string Key { get; }
        public MediaKind Kind { get; }
        public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; }
        public bool CanConvert(string sourceExtension) => true;
        public ConverterAvailability Availability => ConverterAvailability.Ready;
        public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
            => Task.FromResult(ConverterAvailability.Ready);
    }

    private sealed class FakeBaseItem : BaseItem
    {
        private readonly CollectionType? _collectionType;

        public FakeBaseItem(string path, CollectionType? collectionType)
        {
            Path = path;
            _collectionType = collectionType;
        }

        // FormatCompatibilityScanner reads BaseItem.Path and resolves CollectionType via
        // an ILibraryCollectionResolver we inject; the fake resolver in these tests
        // pulls the value straight off this stashed field. See the resolver contract in
        // FormatCompatibilityScanner.cs.
        internal CollectionType? StashedCollectionType => _collectionType;
    }
}
```

- [ ] **Step 2: Run and confirm the tests fail with missing types**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatCompatibilityScannerTests --nologo`
Expected: compile fails with `CS0246: FormatCompatibilityScanner not found`.

---

## Task 6: Create `FormatCompatibilityScanner`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Scanners/FormatCompatibilityScanner.cs`
- Create: `Jellyfin.Plugin.MediaDash/Scanners/ILibraryCollectionResolver.cs`
- Create: `Jellyfin.Plugin.MediaDash/Scanners/LibraryCollectionResolver.cs`

- [ ] **Step 1: Create the collection-type resolver interface**

Rationale: `BaseItem.GetTopParent()` walks a lot of Jellyfin internals. Wrapping it lets tests inject a `CollectionType` without staging a full Jellyfin fixture.

```csharp
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Resolves the CollectionType of the CollectionFolder that owns a given BaseItem.
/// Wrapped as an interface so the format scanner can be unit-tested without a live Jellyfin instance.
/// </summary>
public interface ILibraryCollectionResolver
{
    /// <summary>Returns the CollectionType, or null when it can't be resolved (mixed / not in any library).</summary>
    CollectionType? Resolve(BaseItem item);
}
```

- [ ] **Step 2: Create the production resolver**

```csharp
using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>Production resolver — walks BaseItem.GetTopParent to reach the CollectionFolder.</summary>
public sealed class LibraryCollectionResolver : ILibraryCollectionResolver
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="LibraryCollectionResolver"/> class.</summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    public LibraryCollectionResolver(ILibraryManager libraryManager)
    {
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public CollectionType? Resolve(BaseItem item)
    {
        try
        {
            var top = item.GetTopParent();
            if (top is CollectionFolder cf)
            {
                return cf.CollectionType;
            }

            return null;
        }
        catch (InvalidOperationException)
        {
            // Item detached from the library tree; treat as unresolved.
            return null;
        }
    }
}
```

- [ ] **Step 3: Create the scanner**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Emits FormatCompatibility issues for files whose extension Jellyfin's browser client can't
/// render (or that Jellyfin's server doesn't even index). See design spec §3 for the data flow.
/// Phase 1 is scan-only; the fixer lands in Phase 2+.
/// </summary>
public sealed class FormatCompatibilityScanner : IScanner
{
    private readonly IFormatConverterRegistry _registry;
    private readonly ILibraryCollectionResolver _resolver;
    private readonly ILogger<FormatCompatibilityScanner> _logger;

    /// <summary>Initializes a new instance of the <see cref="FormatCompatibilityScanner"/> class.</summary>
    /// <param name="registry">Converter registry.</param>
    /// <param name="resolver">Library-collection-type resolver.</param>
    /// <param name="logger">Logger.</param>
    public FormatCompatibilityScanner(
        IFormatConverterRegistry registry,
        ILibraryCollectionResolver resolver,
        ILogger<FormatCompatibilityScanner> logger)
    {
        _registry = registry;
        _resolver = resolver;
        _logger = logger;
    }

    // Test-only convenience ctor; production DI uses the 3-arg one above.
    internal FormatCompatibilityScanner(IFormatConverterRegistry registry, ILogger<FormatCompatibilityScanner> logger)
        : this(registry, new TestPathResolver(), logger)
    {
    }

    /// <inheritdoc />
    public IssueType Type => IssueType.FormatCompatibility;

    /// <inheritdoc />
    public Task<IReadOnlyList<Issue>> ScanAsync(
        IReadOnlyList<BaseItem> items,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        var issues = new List<Issue>();
        var now = DateTime.UtcNow;
        var total = items.Count;
        var index = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            if (total > 0)
            {
                progress.Report(100.0 * index / total);
            }

            var path = item.Path;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            var row = FormatMatrix.Classify(path);
            if (row is null || row.ClientRenders || row.TargetExtension is null)
            {
                continue;
            }

            var converter = _registry.Get(row.ConverterKey);
            if (converter is null || !converter.CanConvert(row.Extension))
            {
                continue;
            }

            var libraryType = _resolver.Resolve(item);
            var isMixed = libraryType is null || libraryType == CollectionType.MixedContent;
            if (!isMixed && !converter.AllowedLibraryTypes.Contains(libraryType))
            {
                continue;
            }

            var details = new
            {
                sourceExt = row.Extension,
                targetExt = row.TargetExtension,
                converter = row.ConverterKey,
                libraryKind = libraryType?.ToString() ?? "unknown",
                mixedLibrary = isMixed,
                warnings = isMixed
                    ? new[] { new { blocking = true, code = "mixed_library", message = "Mixed-content library — approve per file." } }
                    : Array.Empty<object>()
            };

            issues.Add(new Issue
            {
                Type = IssueType.FormatCompatibility,
                ItemId = item.Id,
                Path = path,
                DetailsJson = JsonSerializer.Serialize(details),
                SuggestedFix = row.UserAdvice,
                SizeSavings = 0,
                Status = IssueStatus.Detected,
                DetectedAtUtc = now
            });
        }

        return Task.FromResult<IReadOnlyList<Issue>>(issues);
    }

    // ponytail: unit tests pass a FakeBaseItem that stashes the CollectionType on itself; this
    // shim reflects that field via the same accessor. Production always uses the DI resolver.
    private sealed class TestPathResolver : ILibraryCollectionResolver
    {
        public CollectionType? Resolve(BaseItem item)
        {
            var field = item.GetType().GetProperty("StashedCollectionType",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            return field?.GetValue(item) as CollectionType?;
        }
    }
}
```

- [ ] **Step 4: Add `IssueStatus.Detected` import check**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: build succeeds. If it complains about `IssueStatus.Detected` not existing, open `Jellyfin.Plugin.MediaDash/Data/IssueStatus.cs`, note the actual enum name (probably `Detected` — this is the same value `PlayabilityScanner` uses on `Issue.Status`).

- [ ] **Step 5: Run scanner tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatCompatibilityScannerTests --nologo`
Expected: all 7 tests pass.

- [ ] **Step 6: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Scanners/FormatCompatibilityScanner.cs Jellyfin.Plugin.MediaDash/Scanners/ILibraryCollectionResolver.cs Jellyfin.Plugin.MediaDash/Scanners/LibraryCollectionResolver.cs Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityScannerTests.cs
git commit -m "feat(scanners): add FormatCompatibilityScanner (scan-only, no fixer yet)"
```

---

## Task 7: Register the scanner and converter registry in DI

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs`

- [ ] **Step 1: Add the registrations**

Find the block of scanner registrations (they start with `serviceCollection.AddSingleton<IScanner, DuplicateScanner>();`). At the **end** of the scanner list, before `serviceCollection.AddSingleton<LibraryGuard>();`, add:

```csharp
        serviceCollection.AddSingleton<IScanner, FormatCompatibilityScanner>();
        // Converter registry: DI-fed IEnumerable<IFormatConverter> → dictionary. Phase 1 registers
        // no IFormatConverter instances; the registry lands empty and every FormatCompatibility
        // scan silently produces zero issues until Phase 2 wires the first converter.
        serviceCollection.AddSingleton<IFormatConverterRegistry, FormatConverterRegistry>();
        serviceCollection.AddSingleton<ILibraryCollectionResolver, LibraryCollectionResolver>();
```

Also add matching `using` lines at the top of the file if not already there:

```csharp
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
```

- [ ] **Step 2: Build and run the full test suite**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Then: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo`
Expected: build succeeds; all previously-passing tests still pass; the new Format* tests pass.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs
git commit -m "feat(di): register FormatCompatibilityScanner and converter registry"
```

---

## Task 8: Create fixtures directory and README

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/README.md`

- [ ] **Step 1: Create the fixtures README**

```markdown
# Format-Compatibility Fixtures

Tiny real files used by the format-compatibility test harness. Every file is < 200 KB,
CC0 / public-domain / self-created. Provenance is listed below. Fixtures are committed
to the repo — nothing here is generated at test time.

## Sourcing rules

- **Ebook:** Project Gutenberg has Kindle (`.mobi`, `.azw3`), plain-text (`.txt`), RTF, and
  DjVu (via https://www.gutenberg.org/ebooks) editions of most books. Pick the smallest work
  you can find (a poem, a short story). Convert exotic extensions from an EPUB source with
  Calibre and commit the result.
- **Comic:** self-create from three CC0 images (Wikimedia Commons public-domain photos work).
  For CBR, `rar` or `zip` an empty JPG collection; for CB7 use `7z`.
- **Audiobook DRM:** create a stub `sample.aax` from any AAX header pattern — the test only
  asserts the scanner emits a DRM advisory, so the file need not be a valid Audible file.
- **Image:** Wikimedia Commons for HEIC/TIFF/BMP; https://www.raw-samples.ch/ for RAW families.
  Shrink to < 200 KB with `convert -resize 32x32`.
- **Music:** https://modarchive.org/ for tracker files (CC0 subset). For legacy lossless
  (`.ape`, `.wv`, `.tak`, `.tta`, `.wma`), encode from a 1-second sine wave via ffmpeg.
- **Video:** self-create RMVB from a 1-second h264 test pattern via ffmpeg.

## File-size cap

Every fixture < 200 KB. If a format's minimum viable file is bigger, commit a stub and tag
the corresponding [Theory] row `[Trait("Category","LargeFixture")]` — the default CI run
skips that category.

## Provenance

| File | Source | License |
|------|--------|---------|
| `ebook/sample.mobi` | *TBD by engineer during Task 9* | *TBD* |
| ... | | |

Update this table as fixtures are added.
```

- [ ] **Step 2: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/README.md
git commit -m "docs(tests): add fixtures README for format-compatibility test harness"
```

---

## Task 9: Add fixture files

**Files:**
- Create: one small real file per matrix row that has a non-null `TargetExtension`, under `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/<kind>/sample<ext>`.

This task is engineer-hands-on: source real files per Task 8's rules. Each file goes at:

- `fixtures/formats/ebook/sample.mobi`, `sample.azw`, `sample.azw3`, `sample.djvu`, `sample.txt`, `sample.rtf`, `sample.doc`, `sample.docx`, `sample.fb2`, `sample.lit`, `sample.lrf`, `sample.pdb`, `sample.htmlz`, `sample.chm`
- `fixtures/formats/comic/sample.cbr`, `sample.cb7`
- `fixtures/formats/audiobook/sample.aax`
- `fixtures/formats/image/sample.heic`, `sample.heif`, `sample.tif`, `sample.tiff`, `sample.bmp`, `sample.raw`, `sample.nef`, `sample.cr2`, `sample.arw`, `sample.dng`
- `fixtures/formats/music/sample.669`, `sample.it`, `sample.mod`, `sample.s3m`, `sample.xm`, `sample.ape`, `sample.wv`, `sample.tak`, `sample.tta`, `sample.wma`
- `fixtures/formats/video/sample.rmvb`

Update `README.md`'s provenance table for each file as you go.

- [ ] **Step 1: Check total fixture size**

Run: `du -sk Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/` (or PowerShell `Get-ChildItem -Recurse | Measure-Object Length -Sum`)
Expected: < 10 MB total. If any single fixture is > 200 KB, either shrink or tag its future test row `[Trait("Category","LargeFixture")]`.

- [ ] **Step 2: Include fixtures in the test project so they copy to test output**

Modify `Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj`. Inside the last `<ItemGroup>` (or a new one), add:

```xml
  <ItemGroup>
    <None Include="fixtures\**\*.*" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
```

- [ ] **Step 3: Build test project and verify fixtures reach output**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo`
Then: `ls C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\bin\Debug\net9.0\fixtures\formats\ebook\`
Expected: `sample.mobi` (and siblings) present.

- [ ] **Step 4: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/ Jellyfin.Plugin.MediaDash.Tests/Jellyfin.Plugin.MediaDash.Tests.csproj Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/README.md
git commit -m "test(fixtures): add committed sample files for format-compatibility matrix"
```

---

## Task 10: Add `FormatFixtureBase` helper

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatFixtureBase.cs`

- [ ] **Step 1: Write the helper**

```csharp
using System.IO;
using System.Reflection;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

/// <summary>
/// Base class for format-compatibility tests. Owns the fixtures-directory resolver.
/// Fixtures land at `<test-output>/fixtures/formats/<kind>/sample<ext>` via the .csproj
/// glob copy set up in Task 9.
/// </summary>
public abstract class FormatFixtureBase
{
    /// <summary>Absolute path to the fixtures root next to the test assembly.</summary>
    protected static string FixturesRoot { get; } = ResolveFixturesRoot();

    /// <summary>Absolute path to `sample&lt;ext&gt;` for the given kind (lowercase, e.g. "ebook").</summary>
    protected static string FixturePath(string kind, string extensionWithDot)
        => Path.Combine(FixturesRoot, kind, "sample" + extensionWithDot);

    private static string ResolveFixturesRoot()
    {
        var asm = Assembly.GetExecutingAssembly().Location;
        var dir = Path.GetDirectoryName(asm) ?? Directory.GetCurrentDirectory();
        return Path.Combine(dir, "fixtures", "formats");
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/Formats/FormatFixtureBase.cs
git commit -m "test(harness): add FormatFixtureBase — fixture path resolver"
```

---

## Task 11: Write `FormatMatrixCoverageTests`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatMatrixCoverageTests.cs`

- [ ] **Step 1: Write the test**

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

/// <summary>
/// The CI safety net for Phase 1: adding a matrix row without also committing its fixture will
/// fail this test. Phase 2+ will also add "every unrenderable row has a converter registered"
/// once real converters exist; for now the registry is empty so only fixture coverage is checked.
/// </summary>
public class FormatMatrixCoverageTests : FormatFixtureBase
{
    [Fact]
    public void EveryUnrenderableRowWithTargetHasAFixture()
    {
        var missing = new List<string>();
        foreach (var row in FormatMatrix.All.Where(r => !r.ClientRenders && r.TargetExtension is not null))
        {
            var kindDir = row.Kind.ToString().ToLowerInvariant();
            var path = Path.Combine(FixturesRoot, kindDir, "sample" + row.Extension);
            if (!File.Exists(path))
            {
                missing.Add(path);
            }
        }

        Assert.True(missing.Count == 0,
            "Missing fixtures for rows in FormatMatrix. Add each file under Tests/fixtures/formats/<kind>/:\n"
            + string.Join("\n", missing));
    }

    [Fact]
    public void DrmAdvisoryRowsHaveFixtureToo()
    {
        // AAX / AA are unrenderable with a null TargetExtension (DRM advisory). Still want a
        // fixture so Phase 3's scanner test can assert the DRM advisory message is emitted.
        var drmRows = FormatMatrix.All.Where(r =>
            !r.ClientRenders && r.TargetExtension is null && r.Kind == MediaKind.Audiobook);
        var missing = new List<string>();
        foreach (var row in drmRows)
        {
            var path = Path.Combine(FixturesRoot, "audiobook", "sample" + row.Extension);
            if (!File.Exists(path)) missing.Add(path);
        }

        Assert.True(missing.Count == 0,
            "Missing DRM fixture files:\n" + string.Join("\n", missing));
    }
}
```

- [ ] **Step 2: Run**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatMatrixCoverageTests --nologo`
Expected: passes (Task 9 committed every fixture the matrix references). If any fail, the assertion message tells you which files to add.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/Formats/FormatMatrixCoverageTests.cs
git commit -m "test(harness): add FormatMatrixCoverageTests — CI net for missing fixtures"
```

---

## Task 12: Create `CalibreToolchain` scaffold + mock tests

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/IToolchainDownloader.cs`
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/CalibreToolchain.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreToolchainMockTests.cs`

- [ ] **Step 1: Create the downloader interface**

```csharp
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Downloads a platform-appropriate Calibre portable archive to a destination stream.
/// Swapped for a file:// stub in tests. Phase 1 ships no production implementation — the
/// only wired instance is <see cref="NoOpToolchainDownloader"/> which reports unavailability.
/// Phase 2 will add a real HTTPS downloader against Calibre's CDN with a baked-in checksum.
/// </summary>
public interface IToolchainDownloader
{
    /// <summary>True when a downloader is actually configured. Phase 1 default returns false.</summary>
    bool CanDownload { get; }

    /// <summary>Writes the archive bytes into <paramref name="destination"/>. Returns null on failure.</summary>
    Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Default Phase 1 downloader — always reports unavailability, never downloads.</summary>
public sealed class NoOpToolchainDownloader : IToolchainDownloader
{
    /// <inheritdoc />
    public bool CanDownload => false;

    /// <inheritdoc />
    public Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
```

Add the `using System;` line at the top for `IProgress<>`.

- [ ] **Step 2: Create the toolchain scaffold**

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Resolves `ebook-convert` — Calibre's CLI. Phase 1 checks the system PATH and a user-configured
/// override path but never downloads anything (the injected <see cref="IToolchainDownloader"/>
/// is <see cref="NoOpToolchainDownloader"/> by default). Phase 2 wires a real downloader.
/// </summary>
public sealed class CalibreToolchain
{
    private readonly IToolchainDownloader _downloader;
    private readonly ILogger<CalibreToolchain> _logger;
    private readonly string _dataDir;
    private string? _resolvedPath;

    /// <summary>Initializes a new instance of the <see cref="CalibreToolchain"/> class.</summary>
    /// <param name="downloader">Toolchain downloader; NoOpToolchainDownloader in Phase 1.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pluginDataDirectory">Plugin data folder — where downloaded portables extract to.</param>
    public CalibreToolchain(IToolchainDownloader downloader, ILogger<CalibreToolchain> logger, string pluginDataDirectory)
    {
        _downloader = downloader;
        _logger = logger;
        _dataDir = pluginDataDirectory;
    }

    /// <summary>Path to `ebook-convert` when resolved; null when unavailable.</summary>
    public string? EbookConvertPath => _resolvedPath;

    /// <summary>True after a successful resolve.</summary>
    public bool IsAvailable => _resolvedPath is not null;

    /// <summary>
    /// Resolves the toolchain. Order: user-provided override → system PATH → previously-extracted
    /// portable under the plugin data dir → download (Phase 2+; Phase 1's NoOp downloader always fails).
    /// </summary>
    /// <param name="overridePath">User-configured override folder or null.</param>
    /// <param name="progress">Progress reporter for downloads (0..1). Null OK.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ConverterAvailability> EnsureAsync(
        string? overridePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (_resolvedPath is not null && File.Exists(_resolvedPath))
        {
            return ConverterAvailability.Ready;
        }

        // 1. User override.
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var overrideExe = Path.Combine(overridePath, ExecutableName());
            if (File.Exists(overrideExe))
            {
                _resolvedPath = overrideExe;
                return ConverterAvailability.Ready;
            }
        }

        // 2. System PATH.
        var pathExe = FindOnPath(ExecutableName());
        if (pathExe is not null)
        {
            _resolvedPath = pathExe;
            return ConverterAvailability.Ready;
        }

        // 3. Previously-extracted portable under plugin data.
        var extractedExe = Path.Combine(_dataDir, "tools", "calibre", ExecutableName());
        if (File.Exists(extractedExe))
        {
            _resolvedPath = extractedExe;
            return ConverterAvailability.Ready;
        }

        // 4. Download (Phase 1: NoOpToolchainDownloader always returns null).
        if (!_downloader.CanDownload)
        {
            _logger.LogDebug("Calibre unavailable: no downloader wired (Phase 1 default).");
            return ConverterAvailability.Unavailable;
        }

        var archivePath = Path.Combine(_dataDir, "tools", "calibre.download");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (var fs = File.Create(archivePath))
        {
            var checksum = await _downloader.DownloadAsync(fs, progress, cancellationToken).ConfigureAwait(false);
            if (checksum is null)
            {
                _logger.LogWarning("Calibre download failed.");
                return ConverterAvailability.Unavailable;
            }
        }

        // Phase 2 will hook checksum verification + archive extraction here.
        _logger.LogInformation("Calibre download saved to {Path}; extraction not implemented until Phase 2.", archivePath);
        return ConverterAvailability.NeedsDownload;
    }

    private static string ExecutableName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ebook-convert.exe" : "ebook-convert";

    private static string? FindOnPath(string exeName)
    {
        var envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath))
        {
            return null;
        }

        foreach (var dir in envPath.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
```

- [ ] **Step 3: Write mock tests**

Create `Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreToolchainMockTests.cs`:

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class CalibreToolchainMockTests
{
    [Fact]
    public async Task NoOpDownloaderReportsUnavailable()
    {
        using var tmp = new TempDir();
        var chain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(null, null, CancellationToken.None);
        Assert.Equal(ConverterAvailability.Unavailable, result);
        Assert.False(chain.IsAvailable);
    }

    [Fact]
    public async Task UserOverrideResolvesWhenExecutablePresent()
    {
        using var tmp = new TempDir();
        var overrideDir = Path.Combine(tmp.Path, "override");
        Directory.CreateDirectory(overrideDir);
        var exe = Path.Combine(overrideDir, ExeName());
        File.WriteAllText(exe, "stub");

        var chain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(overrideDir, null, CancellationToken.None);

        Assert.Equal(ConverterAvailability.Ready, result);
        Assert.Equal(exe, chain.EbookConvertPath);
    }

    [Fact]
    public async Task PreviouslyExtractedPortableResolves()
    {
        using var tmp = new TempDir();
        var toolsDir = Path.Combine(tmp.Path, "tools", "calibre");
        Directory.CreateDirectory(toolsDir);
        var exe = Path.Combine(toolsDir, ExeName());
        File.WriteAllText(exe, "stub");

        var chain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(null, null, CancellationToken.None);

        Assert.Equal(ConverterAvailability.Ready, result);
        Assert.Equal(exe, chain.EbookConvertPath);
    }

    [Fact]
    public async Task DownloaderInvokedWhenCanDownload()
    {
        using var tmp = new TempDir();
        var invoked = false;
        var downloader = new StubDownloader(() =>
        {
            invoked = true;
            return "fake-checksum";
        });

        var chain = new CalibreToolchain(downloader, NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var result = await chain.EnsureAsync(null, null, CancellationToken.None);

        Assert.True(invoked);
        Assert.Equal(ConverterAvailability.NeedsDownload, result);
    }

    private static string ExeName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ebook-convert.exe" : "ebook-convert";

    private sealed class StubDownloader : IToolchainDownloader
    {
        private readonly Func<string?> _returns;
        public StubDownloader(Func<string?> returns) { _returns = returns; }
        public bool CanDownload => true;
        public Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            destination.WriteByte(0x42);
            return Task.FromResult(_returns());
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid());
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

- [ ] **Step 4: Run the mock tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~CalibreToolchainMockTests --nologo`
Expected: all 4 tests pass.

- [ ] **Step 5: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/Converters/IToolchainDownloader.cs Jellyfin.Plugin.MediaDash/Fixers/Converters/CalibreToolchain.cs Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreToolchainMockTests.cs
git commit -m "feat(converters): add CalibreToolchain scaffold + mock tests (no real download yet)"
```

---

## Task 13: Register `CalibreToolchain` in DI

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs`

- [ ] **Step 1: Add the registrations**

Just after the block from Task 7 (converter registry + resolver), add:

```csharp
        serviceCollection.AddSingleton<IToolchainDownloader, NoOpToolchainDownloader>();
        serviceCollection.AddSingleton(sp => new CalibreToolchain(
            sp.GetRequiredService<IToolchainDownloader>(),
            sp.GetRequiredService<ILogger<CalibreToolchain>>(),
            Plugin.Instance!.ApplicationPaths.PluginConfigurationsPath));
```

Add missing `using` at top if needed:

```csharp
using Microsoft.Extensions.Logging;
```

- [ ] **Step 2: Build and run full test suite**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly && dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo`
Expected: build succeeds; every test passes.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs
git commit -m "feat(di): register CalibreToolchain and no-op downloader (Phase 2 swaps for real)"
```

---

## Task 14: Add `FormatCompatibility` label to the Issues tab

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/issues-tab.js`

- [ ] **Step 1: Locate the type-label mapping**

Run: `grep -n -E "Playability|CorruptNfo|LargeTrickplay" C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Configuration\scripts\tabs\issues-tab.js`
Expected: shows the object literal (or switch) mapping IssueType strings to display strings. Note the object's name (likely `TYPE_LABELS` or `issueTypeLabels`).

- [ ] **Step 2: Add the label**

Add a new entry keyed by `"FormatCompatibility"` with display text `"Unsupported format"` right after the last existing entry. Mirror the surrounding style (trailing comma, quote style).

Example diff (adjust to whatever the actual object looks like):

```js
  ManualDelete: 'Manually deleted',
  FormatCompatibility: 'Unsupported format',
```

- [ ] **Step 3: If there's a fix-icon or column-visibility switch elsewhere, add matching case**

Run: `grep -rn "ManualDelete" C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Configuration\scripts\`
Expected: shows every place that enumerates issue types. Add `FormatCompatibility` alongside `ManualDelete` in each. If a place omits ManualDelete, it probably filters to actionable types only — add `FormatCompatibility` there too; scan-only Issues still need to render.

- [ ] **Step 4: Rebuild plugin so `configPage.html`-referenced JS is fresh**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj --nologo`
Expected: build succeeds.

- [ ] **Step 5: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/issues-tab.js
git commit -m "feat(ui): label FormatCompatibility issues on the Issues tab"
```

---

## Task 15: Update CHANGELOG

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Read the file to find the in-progress section**

Run: `head -40 C:\dev\mediadash\CHANGELOG.md`
Expected: shows the current top version's bullets. If the top version is already released, add a new `## Unreleased` section above it; otherwise append to the existing section.

- [ ] **Step 2: Add the bullet**

Append the bullet under the current in-progress version's bullet list:

```
- Add scan-only Format Compatibility feature scaffold (Phase 1): matrix + scanner + Calibre toolchain stub. Detects files in formats Jellyfin's browser can't render (MOBI, CBR, HEIC, tracker files, etc.). Conversion arrives in Phase 2. F-format-compat.
```

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add CHANGELOG.md
git commit -m "docs(changelog): Phase 1 Format Compatibility scan-only feature"
```

---

## Task 16: Final full-suite regression run

**Files:** (none modified)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo`
Expected: every test passes, including the pre-existing suite. Specifically confirm the new tests pass:

- `FormatMatrixConsistencyTests` (8 tests)
- `FormatMatrixCoverageTests` (2 tests)
- `FormatCompatibilityScannerTests` (7 tests)
- `CalibreToolchainMockTests` (4 tests)

Total added: 21 tests.

- [ ] **Step 2: Build the release-mode plugin as smoke check**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj -c Release --nologo`
Expected: succeeds. Do NOT publish or push — this is scaffold only, no user-facing behavior yet.

- [ ] **Step 3: Report done**

Phase 1 exits with:
- `FormatMatrix` with the full audit (~40 rows).
- `FormatCompatibilityScanner` registered and emitting `FormatCompatibility` issues into `IssueStore`.
- Fixtures committed for every unrenderable row.
- 21 xUnit tests locking the invariants in.
- Calibre toolchain scaffold + mock tests; production downloader is a no-op until Phase 2.
- CHANGELOG entry.
- Issues-tab labels the new type.

No git push. No release build published. Phase 2 (converters + real Calibre download + Settings UI) picks up from here.

---

## Self-Review Notes

- **Spec coverage:**
  - §3 architecture — Tasks 4, 6 cover scanner + converter interface + registry.
  - §4 matrix — Task 3 covers the full data table.
  - §5 library-kind gate — Task 6 (scanner logic) + Task 5 (tests) cover the two-point enforcement.
  - §6 toolchain — Tasks 12–13 ship the scaffold with mock-testable downloader abstraction.
  - §7 test harness — Tasks 2, 5, 8–11 cover consistency, coverage, scanner, and toolchain tests.
  - §8 configuration — **deferred to Phase 2** (Phase 1 is scan-only; nothing to configure yet). Spec approves this phasing.
  - §9 UI — Task 14 does the minimum (label the new IssueType). Full Settings section + Overview card are Phase 2.
  - §10 phased rollout — this plan IS Phase 1; the phase table stays in the spec.
  - §11 error handling — the new `FixReason` values ship with the fixer in Phase 2; Phase 1 has no fixer.
  - §12 analytics — Phase 5 per spec; nothing to do here.

- **Placeholder scan:** the fixtures README table has "TBD by engineer during Task 9" but that's an inline instruction to the engineer, not a plan placeholder — the actual content (sourcing rules + file list) is fully specified in Tasks 8 and 9. Task 15 assumes CHANGELOG has an in-progress section; the step directs the engineer to check and either append or create.

- **Type consistency:** `IssueStatus.Detected`, `FormatEntry`, `MediaKind`, `IFormatConverter`, `ConverterAvailability`, `IFormatConverterRegistry`, `FormatConverterRegistry`, `IToolchainDownloader`, `NoOpToolchainDownloader`, `CalibreToolchain`, `FormatCompatibilityScanner`, `ILibraryCollectionResolver`, `LibraryCollectionResolver`, `FormatFixtureBase` — every name that appears in one task also appears identically in later tasks and matches the spec's terminology.
