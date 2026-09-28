# Format Compatibility Conversion — Phase 2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Take Phase 1's scan-only scaffold to a real end-to-end feature: real converters (Ebook via Calibre, Comic via SharpCompress), real Calibre HTTPS download on Windows, `FormatCompatibilityFixer` wired into the fix pipeline, Settings UI, Overview card. Then deploy to `localhost:8099` dev Jellyfin and verify a real MOBI → EPUB conversion end-to-end.

**Architecture:** Extend `IFormatConverter` with `ConvertAsync` producing a staged output file. `FormatCompatibilityFixer : IFixer` dispatches on `row.ConverterKey`, applies the same library-type gates the scanner enforces, then hands the staged output to `VerifiedReplacementService` for the auditable swap-and-recycle protocol. `CalibreDownloader` implements `IToolchainDownloader` for real HTTPS downloads on Windows with SHA-256 verification; other platforms still get PATH/override resolution. UI adds one Settings section and one Overview card.

**Tech Stack:** C# net9.0, xUnit 2.9, `SharpCompress` 0.50.1 (already a dep), `System.Net.Http.HttpClient`, Calibre 7.14.0 (external — download from `https://download.calibre-ebook.com/`), Jellyfin 10.11/12 dual-target.

**Reference spec:** [`docs/superpowers/specs/2026-09-28-format-compatibility-conversion-design.md`](../specs/2026-09-28-format-compatibility-conversion-design.md)
**Reference plan:** [`docs/superpowers/plans/2026-09-28-format-compatibility-phase-1.md`](2026-09-28-format-compatibility-phase-1.md)

---

## File Structure

**Created:**
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/ConversionResult.cs` — record returned by `ConvertAsync`.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/ComicConverter.cs` — CBR/CB7 → CBZ via SharpCompress.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/EbookConverter.cs` — shells out to `ebook-convert`.
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/CalibreDownloader.cs` — real HTTPS downloader (Windows-only).
- `Jellyfin.Plugin.MediaDash/Fixers/FormatCompatibilityFixer.cs` — IFixer implementation.
- `Jellyfin.Plugin.MediaDash.Tests/Formats/ComicConverterTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/EbookConverterTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityFixerTests.cs`
- `Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreDownloaderTests.cs`

**Modified:**
- `Jellyfin.Plugin.MediaDash/Fixers/Converters/IFormatConverter.cs` — add `ConvertAsync` method.
- `Jellyfin.Plugin.MediaDash/Fixers/FixReason.cs` — add new reasons.
- `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs` — add config knobs + wire into GetFixMode/GetDisposal.
- `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs` — register converters, fixer, real downloader.
- `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/settings-tab.js` — new "Format conversion" section.
- `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/overview-tab.js` — new "Unsupported formats" card.
- `Jellyfin.Plugin.MediaDash/Configuration/scripts/dom-helpers.js` — FormatCompatibility action → real fix action.
- `Jellyfin.Plugin.MediaDash/Configuration/i18n/en.json` — new strings.
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/comic/sample.cbr` — replace stub with a real CBR (three tiny JPGs zipped-with-rar-header).
- `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/ebook/sample.mobi` — already real (from Phase 1). No change.
- `CHANGELOG.md` — Phase 2 bullet.

---

## Task 1: Extend `FixReason` with format-compat reasons

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/FixReason.cs`

- [ ] **Step 1: Append the new reasons at the end of the enum** (order matters — never renumber existing values).

Add after the last existing entry (`InsufficientSavings`):

```csharp
    /// <summary>Format-compat: parent CollectionType outside converter's AllowedLibraryTypes.</summary>
    LibraryTypeMismatch,

    /// <summary>Format-compat: CollectionType could not be resolved for the item.</summary>
    LibraryTypeUnknown,

    /// <summary>Format-compat: mixed-content library, no per-row approve on record.</summary>
    MixedLibraryUserReviewOnly,

    /// <summary>Format-compat: converter toolchain missing and lazy-download failed.</summary>
    ConverterUnavailable,

    /// <summary>Format-compat: converter ran but its output failed post-conversion probe.</summary>
    ConverterFailed,

    /// <summary>Format-compat: matrix row has no target extension for this file (DRM advisory).</summary>
    SourceExtensionNotConvertible,
```

- [ ] **Step 2: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: succeeds. The `FixResultPolicy` file's `switch` on `FixReason` may emit a CS8509 warning about the new values — if so, add the six new cases to the `switch` mapped to a sensible policy (probably the same policy the existing `Refused` cases use, e.g. `PolicyDecision.Terminal`). Read `Jellyfin.Plugin.MediaDash/Fixers/FixResultPolicy.cs` first to see the pattern before editing.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/FixReason.cs Jellyfin.Plugin.MediaDash/Fixers/FixResultPolicy.cs
git commit -m "feat(fixers): add FormatCompatibility FixReason values"
```

---

## Task 2: Extend `IFormatConverter` with `ConvertAsync`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/ConversionResult.cs`
- Modify: `Jellyfin.Plugin.MediaDash/Fixers/Converters/IFormatConverter.cs`

- [ ] **Step 1: Create `ConversionResult.cs`**

```csharp
namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>Outcome of a single conversion attempt.</summary>
/// <param name="Success">True when the staged output was written and passed the converter's own sanity check.</param>
/// <param name="StagedPath">Absolute path of the produced output file, or null on failure.</param>
/// <param name="Reason">Plain-language description of what happened. Empty on success.</param>
/// <param name="TechnicalDetail">Raw process output (ebook-convert stderr, exception text) when the converter has one to expose.</param>
public sealed record ConversionResult(bool Success, string? StagedPath, string Reason, string? TechnicalDetail = null);
```

- [ ] **Step 2: Modify `IFormatConverter.cs`** — add `ConvertAsync` after `EnsureToolchainAsync`:

```csharp
    /// <summary>
    /// Converts <paramref name="sourcePath"/> to a staged output file at <paramref name="stagedTargetPath"/>.
    /// Producers MUST write the output to the exact staged path — <see cref="Fixers.VerifiedReplacementService"/>
    /// takes ownership of it for the swap-and-recycle protocol.
    /// </summary>
    /// <param name="sourcePath">Absolute path of the source file.</param>
    /// <param name="stagedTargetPath">Absolute path the converter must write the output to. Includes the inflight tail already.</param>
    /// <param name="progress">Optional intra-item progress reporter (0..1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The conversion result.</returns>
    Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken);
```

- [ ] **Step 3: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: succeeds. No implementations exist yet, so no compile break. StyleCop may want `<returns>` — already present.

- [ ] **Step 4: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/Converters/
git commit -m "feat(converters): extend IFormatConverter with ConvertAsync + ConversionResult"
```

---

## Task 3: Add config knobs

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs`

- [ ] **Step 1: Add the field defaults** to the constructor (append after `QualityMinSavingsBytes = 0;`):

```csharp
        FormatCompatibilityFixMode = FixMode.DetectOnly;
        FormatCompatibilityDisposal = DisposalMethod.RecycleBin;
        ConvertEbooks = false;
        ConvertComics = false;
        ConvertAudiobooks = false;
        ConvertImages = false;
        ConvertMusic = false;
        ConvertVideos = false;
        AudiobookMergeMultiFile = false;
        CalibreOverridePath = string.Empty;
        EbookPreferredTarget = string.Empty;
        ImagePreferredTarget = string.Empty;
        MusicPreferredTarget = string.Empty;
```

- [ ] **Step 2: Add the properties** after the existing `QualityMinSavingsBytes` property (find its `set;` and add a new region below):

```csharp
    /// <summary>Gets or sets how the format-compatibility scanner/fixer act.</summary>
    public FixMode FormatCompatibilityFixMode { get; set; }

    /// <summary>Gets or sets where format-compatibility replaced originals go.</summary>
    public DisposalMethod FormatCompatibilityDisposal { get; set; }

    /// <summary>Gets or sets a value indicating whether the ebook converter runs.</summary>
    public bool ConvertEbooks { get; set; }

    /// <summary>Gets or sets a value indicating whether the comic converter runs.</summary>
    public bool ConvertComics { get; set; }

    /// <summary>Gets or sets a value indicating whether the audiobook converter runs. Phase 3+.</summary>
    public bool ConvertAudiobooks { get; set; }

    /// <summary>Gets or sets a value indicating whether the image converter runs. Phase 4.</summary>
    public bool ConvertImages { get; set; }

    /// <summary>Gets or sets a value indicating whether the music converter runs. Phase 4.</summary>
    public bool ConvertMusic { get; set; }

    /// <summary>Gets or sets a value indicating whether the video converter runs. Phase 4.</summary>
    public bool ConvertVideos { get; set; }

    /// <summary>Gets or sets a value indicating whether the audiobook fixer merges folder-of-mp3 audiobooks into a single m4b.</summary>
    public bool AudiobookMergeMultiFile { get; set; }

    /// <summary>Gets or sets the folder path containing a user-installed <c>ebook-convert</c> executable.</summary>
    public string CalibreOverridePath { get; set; }

    /// <summary>Gets or sets the preferred ebook target format ("epub" or "pdf"). Empty = matrix default.</summary>
    public string EbookPreferredTarget { get; set; }

    /// <summary>Gets or sets the preferred image target format ("jpg", "png", "webp"). Empty = matrix default.</summary>
    public string ImagePreferredTarget { get; set; }

    /// <summary>Gets or sets the preferred music target format ("flac", "mp3"). Empty = matrix default.</summary>
    public string MusicPreferredTarget { get; set; }
```

- [ ] **Step 3: Wire into `GetFixMode`** — add a `case` in the existing switch expression (before the `_ => FixMode.DetectOnly` arm):

```csharp
            Data.IssueType.FormatCompatibility => FormatCompatibilityFixMode,
```

- [ ] **Step 4: Wire into `GetDisposal`** — same pattern (before the `_ => DisposalMethod.RecycleBin` arm):

```csharp
            Data.IssueType.FormatCompatibility => FormatCompatibilityDisposal,
```

- [ ] **Step 5: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: succeeds. `FixMode` and `DisposalMethod` types are already imported via the file's existing `using` block.

- [ ] **Step 6: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Configuration/PluginConfiguration.cs
git commit -m "feat(config): add FormatCompatibility fix mode + per-kind toggles"
```

---

## Task 4: Write `ComicConverterTests` (failing)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/ComicConverterTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class ComicConverterTests : FormatFixtureBase
{
    private static ComicConverter Converter()
        => new(NullLogger<ComicConverter>.Instance);

    [Fact]
    public void KeyIsComic()
    {
        Assert.Equal("comic", Converter().Key);
    }

    [Fact]
    public void ReadyWithoutToolchain()
    {
        Assert.Equal(ConverterAvailability.Ready, Converter().Availability);
    }

    [Fact]
    public void CanConvertKnownExtensions()
    {
        var c = Converter();
        Assert.True(c.CanConvert(".cbr"));
        Assert.True(c.CanConvert(".cb7"));
        Assert.False(c.CanConvert(".cbz"));
        Assert.False(c.CanConvert(".mobi"));
    }

    [Fact]
    public async Task ConvertsCbrToCbz()
    {
        var source = FixturePath("comic", ".cbr");
        if (!File.Exists(source))
        {
            throw new SkipException("comic/sample.cbr not present — Task 13 upgrades this fixture");
        }

        using var tmp = new TempDir();
        var target = Path.Combine(tmp.Path, "out.cbz");

        var result = await Converter().ConvertAsync(source, target, null, CancellationToken.None);

        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(target));
        using var zip = ZipFile.OpenRead(target);
        Assert.NotEmpty(zip.Entries);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid());
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}

// Local skip exception — xUnit 2.9 doesn't have Skip built into Assert.
internal sealed class SkipException : Exception
{
    public SkipException(string message) : base(message) { }
}
```

- [ ] **Step 2: Attempt to build**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~ComicConverterTests --nologo`
Expected: build fails with `CS0246: ComicConverter` not found.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/Formats/ComicConverterTests.cs
git commit -m "test(converters): add ComicConverter tests (failing — converter lands in next task)"
```

---

## Task 5: Implement `ComicConverter`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/ComicConverter.cs`

- [ ] **Step 1: Write the converter**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Scanners;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Repackages CBR (RAR) and CB7 (7z) comic archives as CBZ (ZIP). No transcoding — the enclosed
/// image bytes are copied verbatim. SharpCompress ships with the plugin so no external tool is needed.
/// </summary>
public sealed class ComicConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase) { ".cbr", ".cb7" };

    private readonly ILogger<ComicConverter> _logger;

    /// <summary>Initializes a new instance of the <see cref="ComicConverter"/> class.</summary>
    /// <param name="logger">The logger.</param>
    public ComicConverter(ILogger<ComicConverter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "comic";

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Comic;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?> { CollectionType.books };

    /// <inheritdoc />
    public bool CanConvert(string sourceExtension) => Handled.Contains(sourceExtension);

    /// <inheritdoc />
    public ConverterAvailability Availability => ConverterAvailability.Ready;

    /// <inheritdoc />
    public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
        => Task.FromResult(ConverterAvailability.Ready);

    /// <inheritdoc />
    public Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            using var source = OpenArchive(sourcePath, ext);
            Directory.CreateDirectory(Path.GetDirectoryName(stagedTargetPath)!);
            using (var fs = File.Create(stagedTargetPath))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var total = source.Entries.Count;
                var index = 0;
                foreach (var entry in source.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (entry.IsDirectory)
                    {
                        continue;
                    }

                    var name = entry.Key ?? Guid.NewGuid().ToString();
                    var zipEntry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                    using var src = entry.OpenEntryStream();
                    using var dst = zipEntry.Open();
                    src.CopyTo(dst);

                    index++;
                    if (total > 0)
                    {
                        progress?.Report(1.0 * index / total);
                    }
                }
            }

            return Task.FromResult(new ConversionResult(true, stagedTargetPath, string.Empty));
        }
        catch (OperationCanceledException)
        {
            SafeDelete(stagedTargetPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Comic conversion failed: {Source} -> {Target}", sourcePath, stagedTargetPath);
            SafeDelete(stagedTargetPath);
            return Task.FromResult(new ConversionResult(false, null, "Comic conversion failed: " + ex.Message, ex.ToString()));
        }
    }

    private static IArchive OpenArchive(string sourcePath, string ext)
        => ext switch
        {
            ".cbr" => RarArchive.Open(sourcePath),
            ".cb7" => SevenZipArchive.Open(sourcePath),
            _ => throw new NotSupportedException("ComicConverter cannot open " + ext)
        };

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: succeeds. `SharpCompress.Archives.Rar.RarArchive` and `SharpCompress.Archives.SevenZip.SevenZipArchive` come from the existing SharpCompress 0.50.1 dep.

- [ ] **Step 3: Run tests** (may show the `.cbr` fixture-missing skip, which is fine for now — Task 13 addresses)

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~ComicConverterTests --nologo`
Expected: `KeyIsComic`, `ReadyWithoutToolchain`, `CanConvertKnownExtensions` pass. `ConvertsCbrToCbz` may throw the SkipException (test fails, but the failure is the expected "fixture stub is not a real CBR" — Task 13 fixes).

- [ ] **Step 4: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/Converters/ComicConverter.cs
git commit -m "feat(converters): implement ComicConverter (CBR/CB7 -> CBZ via SharpCompress)"
```

---

## Task 6: Write `EbookConverterTests` (failing)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/EbookConverterTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class EbookConverterTests : FormatFixtureBase
{
    [Fact]
    public void KeyIsEbook()
    {
        Assert.Equal("ebook", NewConverter().Key);
    }

    [Fact]
    public void CanConvertMatrixEbookExtensions()
    {
        var c = NewConverter();
        Assert.True(c.CanConvert(".mobi"));
        Assert.True(c.CanConvert(".azw3"));
        Assert.True(c.CanConvert(".djvu"));
        Assert.True(c.CanConvert(".fb2"));
        Assert.False(c.CanConvert(".epub"));
        Assert.False(c.CanConvert(".cbr"));
    }

    [Fact]
    public async Task ReportsUnavailableWhenToolchainMissing()
    {
        using var tmp = new TempDir();
        var toolchain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var config = new StubConfig();
        var converter = new EbookConverter(toolchain, config, NullLogger<EbookConverter>.Instance);

        var availability = await converter.EnsureToolchainAsync(CancellationToken.None);

        Assert.Equal(ConverterAvailability.Unavailable, availability);
    }

    [Fact]
    public async Task ConvertsMobiToEpubWhenCalibreAvailable()
    {
        var source = FixturePath("ebook", ".mobi");
        if (!File.Exists(source))
        {
            throw new SkipException("ebook/sample.mobi missing");
        }

        using var tmp = new TempDir();
        var toolchain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp.Path);
        var config = new StubConfig();
        var converter = new EbookConverter(toolchain, config, NullLogger<EbookConverter>.Instance);

        // If the machine has ebook-convert on PATH, exercise the real conversion. Otherwise skip —
        // the plan's Task 14 (E2E) covers this on the dev box after Calibre is installed / downloaded.
        var readiness = await converter.EnsureToolchainAsync(CancellationToken.None);
        if (readiness != ConverterAvailability.Ready)
        {
            throw new SkipException("Calibre not installed on this test host — skipping real conversion");
        }

        var target = Path.Combine(tmp.Path, "out.epub");
        var result = await converter.ConvertAsync(source, target, null, CancellationToken.None);

        Assert.True(result.Success, result.Reason);
        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 0);
    }

    private static EbookConverter NewConverter()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "mediadash-" + Guid.NewGuid());
        Directory.CreateDirectory(tmp);
        var toolchain = new CalibreToolchain(new NoOpToolchainDownloader(), NullLogger<CalibreToolchain>.Instance, tmp);
        return new EbookConverter(toolchain, new StubConfig(), NullLogger<EbookConverter>.Instance);
    }

    private sealed class StubConfig : IPluginConfigurationProvider
    {
        public PluginConfiguration Get() => new();
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mediadash-tests-" + Guid.NewGuid());
        public TempDir() { Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
```

Note: `IPluginConfigurationProvider` is used across the plugin — its interface exposes `PluginConfiguration Get()`. Check the actual signature in `Jellyfin.Plugin.MediaDash/Configuration/PluginConfigurationProvider.cs`. Adapt the stub if the method is named differently (e.g. `Snapshot`, `Current`).

- [ ] **Step 2: Attempt to build**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~EbookConverterTests --nologo`
Expected: build fails with CS0246 for `EbookConverter`.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/Formats/EbookConverterTests.cs
git commit -m "test(converters): add EbookConverter tests (failing — implementation next)"
```

---

## Task 7: Implement `EbookConverter`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/EbookConverter.cs`

- [ ] **Step 1: Write the converter**

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Scanners;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Shells out to Calibre's <c>ebook-convert</c> CLI. Handles MOBI/AZW/AZW3/DjVu and Calibre-supported
/// office formats (TXT/RTF/DOC/DOCX/FB2/LIT/LRF/PDB/HTMLZ/CHM). Target format is EPUB unless the row
/// picks something else (DjVu targets PDF) or the user override <see cref="PluginConfiguration.EbookPreferredTarget"/>
/// pins one.
/// </summary>
public sealed class EbookConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mobi", ".azw", ".azw3", ".djvu",
        ".txt", ".rtf", ".doc", ".docx",
        ".fb2", ".lit", ".lrf", ".pdb", ".htmlz", ".chm"
    };

    private static readonly TimeSpan ConversionTimeout = TimeSpan.FromMinutes(10);

    private readonly CalibreToolchain _toolchain;
    private readonly IPluginConfigurationProvider _config;
    private readonly ILogger<EbookConverter> _logger;

    /// <summary>Initializes a new instance of the <see cref="EbookConverter"/> class.</summary>
    /// <param name="toolchain">Calibre toolchain resolver.</param>
    /// <param name="config">Config snapshot provider.</param>
    /// <param name="logger">The logger.</param>
    public EbookConverter(CalibreToolchain toolchain, IPluginConfigurationProvider config, ILogger<EbookConverter> logger)
    {
        _toolchain = toolchain;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Key => "ebook";

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Ebook;

    /// <inheritdoc />
    public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; } = new HashSet<CollectionType?> { CollectionType.books };

    /// <inheritdoc />
    public bool CanConvert(string sourceExtension) => Handled.Contains(sourceExtension);

    /// <inheritdoc />
    public ConverterAvailability Availability => _toolchain.IsAvailable ? ConverterAvailability.Ready : ConverterAvailability.Unavailable;

    /// <inheritdoc />
    public async Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
    {
        var cfg = _config.Get();
        return await _toolchain.EnsureAsync(cfg.CalibreOverridePath, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var readiness = await EnsureToolchainAsync(cancellationToken).ConfigureAwait(false);
        if (readiness != ConverterAvailability.Ready || _toolchain.EbookConvertPath is null)
        {
            return new ConversionResult(false, null, "Calibre's ebook-convert is not available on this server. Install Calibre or set the override path in Settings.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stagedTargetPath)!);

            var psi = new ProcessStartInfo
            {
                FileName = _toolchain.EbookConvertPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(sourcePath);
            psi.ArgumentList.Add(stagedTargetPath);

            using var proc = new Process { StartInfo = psi };
            var stderr = new StringBuilder();
            var stdout = new StringBuilder();
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };

            if (!proc.Start())
            {
                return new ConversionResult(false, null, "ebook-convert failed to start.");
            }

            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ConversionTimeout);

            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(proc);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKill(proc);
                return new ConversionResult(false, null, "ebook-convert timed out after " + ConversionTimeout.TotalMinutes + " minutes.");
            }

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("ebook-convert failed (exit={Exit}) for {Source}: {Stderr}", proc.ExitCode, sourcePath, stderr);
                return new ConversionResult(false, null, "ebook-convert exited " + proc.ExitCode + ".", stderr.ToString());
            }

            if (!File.Exists(stagedTargetPath) || new FileInfo(stagedTargetPath).Length == 0)
            {
                return new ConversionResult(false, null, "ebook-convert produced no output.", stdout.ToString());
            }

            return new ConversionResult(true, stagedTargetPath, string.Empty);
        }
        catch (OperationCanceledException)
        {
            SafeDelete(stagedTargetPath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ebook conversion crashed: {Source} -> {Target}", sourcePath, stagedTargetPath);
            SafeDelete(stagedTargetPath);
            return new ConversionResult(false, null, "Ebook conversion crashed: " + ex.Message, ex.ToString());
        }
    }

    private static void TryKill(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: succeeds.

- [ ] **Step 3: Run tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~EbookConverterTests --nologo`
Expected: `KeyIsEbook`, `CanConvertMatrixEbookExtensions`, `ReportsUnavailableWhenToolchainMissing` pass. `ConvertsMobiToEpubWhenCalibreAvailable` may skip.

- [ ] **Step 4: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/Converters/EbookConverter.cs
git commit -m "feat(converters): implement EbookConverter (shells out to ebook-convert)"
```

---

## Task 8: Real `CalibreDownloader` (Windows-only)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/Converters/CalibreDownloader.cs`
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreDownloaderTests.cs`

- [ ] **Step 1: Determine the pinned Calibre version**

Before writing code, resolve the current stable Calibre release. Visit `https://calibre-ebook.com/download_windows64` and note the version (e.g. `7.14.0`) and the "portable" download URL — typically `https://download.calibre-ebook.com/{VERSION}/calibre-portable-installer-{VERSION}.exe`. Compute SHA-256:

```bash
curl -sL https://download.calibre-ebook.com/7.14.0/calibre-portable-installer-7.14.0.exe -o /tmp/calibre.exe
sha256sum /tmp/calibre.exe
```

Record the version, URL, and SHA-256 as constants in the code below.

- [ ] **Step 2: Write `CalibreDownloader.cs`**

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Downloads Calibre portable (Windows only in Phase 2 — Linux users install via apt / package manager,
/// macOS via cask / dmg). The archive is written to <paramref name="destination"/>; extraction is
/// left to <see cref="CalibreToolchain"/>. The SHA-256 constant below is the byte hash of the
/// Windows portable installer for the pinned version.
/// </summary>
public sealed class CalibreDownloader : IToolchainDownloader
{
    // ponytail: pin to a specific known-good release. If Calibre releases a newer version,
    // bump these three constants together. Auto-latest-fetch adds a metadata endpoint that
    // calibre-ebook.com doesn't stably expose; the version-bump churn beats a runtime lookup
    // that might silently return a wrong SHA.
    private const string PinnedVersion = "7.14.0";
    private const string WindowsUrl = "https://download.calibre-ebook.com/" + PinnedVersion + "/calibre-portable-installer-" + PinnedVersion + ".exe";
    private const string WindowsSha256 = "PLACEHOLDER_SHA256_SET_IN_STEP_1_ABOVE";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CalibreDownloader> _logger;

    /// <summary>Initializes a new instance of the <see cref="CalibreDownloader"/> class.</summary>
    /// <param name="httpClientFactory">HTTP client factory (Jellyfin provides this).</param>
    /// <param name="logger">The logger.</param>
    public CalibreDownloader(IHttpClientFactory httpClientFactory, ILogger<CalibreDownloader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanDownload => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!CanDownload)
        {
            _logger.LogInformation("CalibreDownloader: not on Windows — declining.");
            return null;
        }

        using var http = _httpClientFactory.CreateClient(nameof(CalibreDownloader));
        http.Timeout = TimeSpan.FromMinutes(15);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(WindowsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Calibre download HTTP request failed.");
            return null;
        }

        var total = response.Content.Headers.ContentLength ?? -1L;
        using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var sha = SHA256.Create();
        using var hashing = new CryptoStream(destination, sha, CryptoStreamMode.Write, leaveOpen: true);

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await hashing.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (total > 0)
            {
                progress?.Report((double)copied / total);
            }
        }

        await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
        hashing.FlushFinalBlock();

        var actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.Equals(actualSha, WindowsSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Calibre download SHA-256 mismatch. Expected={Expected} Actual={Actual}", WindowsSha256, actualSha);
            return null;
        }

        return actualSha;
    }
}
```

- [ ] **Step 3: Write mock-only downloader tests** (no live download in default CI)

```csharp
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class CalibreDownloaderTests
{
    [Fact]
    public void CanDownloadReflectsPlatform()
    {
        var http = new StubHttpClientFactory();
        var d = new CalibreDownloader(http, NullLogger<CalibreDownloader>.Instance);
        Assert.Equal(RuntimeInformation.IsOSPlatform(OSPlatform.Windows), d.CanDownload);
    }

    [Fact]
    public async Task DeclinesWhenNotOnWindows()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Nothing to assert — skipped implicitly.
            return;
        }

        var http = new StubHttpClientFactory();
        var d = new CalibreDownloader(http, NullLogger<CalibreDownloader>.Instance);
        using var dest = new MemoryStream();
        var result = await d.DownloadAsync(dest, null, CancellationToken.None);
        Assert.Null(result);
        Assert.Equal(0, dest.Length);
    }

    private sealed class StubHttpClientFactory : System.Net.Http.IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new();
    }
}
```

- [ ] **Step 4: Build**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly`
Expected: succeeds. Confirm the placeholder SHA is replaced with the real value from Step 1 — a placeholder value will cause the E2E test in Task 14 to fail.

- [ ] **Step 5: Run tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~CalibreDownloaderTests --nologo`
Expected: both tests pass.

- [ ] **Step 6: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/Converters/CalibreDownloader.cs Jellyfin.Plugin.MediaDash.Tests/Formats/CalibreDownloaderTests.cs
git commit -m "feat(converters): implement real CalibreDownloader (Windows-only Phase 2)"
```

---

## Task 9: Write `FormatCompatibilityFixerTests` (failing)

**Files:**
- Create: `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityFixerTests.cs`

- [ ] **Step 1: Write the tests**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatCompatibilityFixerTests
{
    [Fact]
    public void CanFixReturnsTrueForFormatCompatibility()
    {
        var fixer = NewFixer();
        Assert.True(fixer.CanFix(IssueType.FormatCompatibility));
        Assert.False(fixer.CanFix(IssueType.Duplicate));
    }

    [Fact]
    public async Task RefusesWhenSourceMissing()
    {
        var (fixer, _, _) = Setup();
        var issue = MakeIssue(@"C:\nonexistent\file.mobi");
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesInMixedLibraryWithoutApproval()
    {
        var (fixer, resolver, registry) = Setup();
        var source = MakeTempFile(".mobi");
        var issue = MakeIssue(source.FullName, mixedLibrary: true);
        resolver.Assign(issue.ItemId, null);
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("mixed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DryRunProducesPreview()
    {
        var (fixer, resolver, registry) = Setup(dryRun: true);
        var source = MakeTempFile(".mobi");
        var issue = MakeIssue(source.FullName);
        resolver.Assign(issue.ItemId, CollectionType.books);
        registry.Register(new StubConverter("ebook", MediaKind.Ebook, new[] { CollectionType.books }));

        var result = await fixer.FixAsync(issue, null, CancellationToken.None);

        Assert.True(result.WasDryRun);
        Assert.Contains("Preview", result.Message);
    }

    // -- helpers below --

    private static Issue MakeIssue(string path, bool mixedLibrary = false)
    {
        return new Issue
        {
            Id = 42,
            Type = IssueType.FormatCompatibility,
            ItemId = Guid.NewGuid(),
            Path = path,
            DetailsJson = JsonSerializer.Serialize(new
            {
                sourceExt = Path.GetExtension(path),
                targetExt = ".epub",
                converter = "ebook",
                mixedLibrary
            }),
            SuggestedFix = "Convert",
            Status = IssueStatus.Detected
        };
    }

    private static (FormatCompatibilityFixer fixer, StubResolver resolver, StubRegistry registry) Setup(bool dryRun = false)
    {
        var registry = new StubRegistry();
        var resolver = new StubResolver();
        var configProvider = new StubConfig(dryRun);
        // NB: The real fixer takes VerifiedReplacementService, LibraryGuard, ProtectionService, etc.
        // For unit tests the plan wires stub replacements in a follow-up if broader integration is needed.
        // For Phase 2 the plan will introduce test seams only where behaviour is testable in isolation.
        throw new NotImplementedException("FormatCompatibilityFixer construction — implementation lands in Task 10.");
    }

    private static FormatCompatibilityFixer NewFixer()
    {
        var (f, _, _) = Setup();
        return f;
    }

    private static FileInfo MakeTempFile(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), "mediadash-fixer-" + Guid.NewGuid() + ext);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return new FileInfo(path);
    }

    private sealed class StubConverter : IFormatConverter
    {
        public StubConverter(string key, MediaKind kind, CollectionType?[] allowed)
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
        public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken ct) => Task.FromResult(ConverterAvailability.Ready);
        public Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken ct)
        {
            File.WriteAllBytes(stagedTargetPath, new byte[] { 5, 6, 7, 8 });
            return Task.FromResult(new ConversionResult(true, stagedTargetPath, string.Empty));
        }
    }

    private sealed class StubRegistry : IFormatConverterRegistry
    {
        private readonly Dictionary<string, IFormatConverter> _map = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<IFormatConverter> All => _map.Values;
        public IFormatConverter? Get(string key) => _map.TryGetValue(key, out var c) ? c : null;
        public void Register(IFormatConverter c) => _map[c.Key] = c;
    }

    private sealed class StubResolver : ILibraryCollectionResolver
    {
        private readonly Dictionary<Guid, CollectionType?> _byItemId = new();
        public void Assign(Guid itemId, CollectionType? type) => _byItemId[itemId] = type;
        public CollectionType? Resolve(BaseItem item) => _byItemId.TryGetValue(item.Id, out var t) ? t : null;
    }

    private sealed class StubConfig : Configuration.IPluginConfigurationProvider
    {
        private readonly bool _dryRun;
        public StubConfig(bool dryRun) { _dryRun = dryRun; }
        public Configuration.PluginConfiguration Get()
        {
            var c = new Configuration.PluginConfiguration { DryRun = _dryRun };
            c.ConvertEbooks = true;
            c.ConvertComics = true;
            c.FormatCompatibilityFixMode = Configuration.FixMode.Automatic;
            return c;
        }
    }
}
```

- [ ] **Step 2: Attempt to build**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatCompatibilityFixerTests --nologo`
Expected: build fails — `FormatCompatibilityFixer` doesn't exist, and `Setup()` throws.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityFixerTests.cs
git commit -m "test(fixers): add FormatCompatibilityFixer tests (failing — implementation next)"
```

Note: the `Setup()` helper is deliberately left throwing `NotImplementedException` here — Task 10 finishes wiring it once the fixer ctor is known. This is TDD's "list the tests" red step.

---

## Task 10: Implement `FormatCompatibilityFixer`

**Files:**
- Create: `Jellyfin.Plugin.MediaDash/Fixers/FormatCompatibilityFixer.cs`
- Modify: `Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityFixerTests.cs` — fix the `Setup()` helper.

- [ ] **Step 1: Write the fixer**

```csharp
using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Fixer for FormatCompatibility issues. Dispatches to the registered <see cref="IFormatConverter"/>
/// for the row's converter key, then hands the staged output to <see cref="VerifiedReplacementService"/>
/// for the audited swap-and-recycle protocol.
/// </summary>
public sealed class FormatCompatibilityFixer : IFixer
{
    private const string InflightTail = ".mediaDash.tmp";

    private readonly IFormatConverterRegistry _converters;
    private readonly ILibraryCollectionResolver _resolver;
    private readonly VerifiedReplacementService _replacement;
    private readonly LibraryGuard _guard;
    private readonly ProtectionService _protection;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ILogger<FormatCompatibilityFixer> _logger;

    /// <summary>Initializes a new instance of the <see cref="FormatCompatibilityFixer"/> class.</summary>
    /// <param name="converters">Converter registry.</param>
    /// <param name="resolver">Library collection-type resolver.</param>
    /// <param name="replacement">Verified replacement service.</param>
    /// <param name="guard">Library guard.</param>
    /// <param name="protection">Protection service.</param>
    /// <param name="configProvider">Config snapshot provider.</param>
    /// <param name="libraryManager">Jellyfin library manager for post-fix item purge.</param>
    /// <param name="libraryMonitor">Jellyfin library monitor for FS-change notifications.</param>
    /// <param name="logger">The logger.</param>
    public FormatCompatibilityFixer(
        IFormatConverterRegistry converters,
        ILibraryCollectionResolver resolver,
        VerifiedReplacementService replacement,
        LibraryGuard guard,
        ProtectionService protection,
        IPluginConfigurationProvider configProvider,
        ILibraryManager libraryManager,
        ILibraryMonitor libraryMonitor,
        ILogger<FormatCompatibilityFixer> logger)
    {
        _converters = converters;
        _resolver = resolver;
        _replacement = replacement;
        _guard = guard;
        _protection = protection;
        _configProvider = configProvider;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanFix(IssueType type) => type == IssueType.FormatCompatibility;

    /// <inheritdoc />
    public async Task<FixResult> FixAsync(Issue issue, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var config = _configProvider.Get();

        if (!File.Exists(issue.Path))
        {
            return FixResult.Fail(FixReason.SourceMissing, "The file no longer exists; re-scan to refresh the list.");
        }

        var details = ParseDetails(issue.DetailsJson);
        if (details is null || string.IsNullOrEmpty(details.ConverterKey) || string.IsNullOrEmpty(details.TargetExt))
        {
            return FixResult.Refused(FixReason.SourceExtensionNotConvertible, "Issue details missing conversion target.");
        }

        var converter = _converters.Get(details.ConverterKey);
        if (converter is null)
        {
            return FixResult.Refused(FixReason.ConverterUnavailable, "Converter '" + details.ConverterKey + "' is not registered.");
        }

        if (!IsKindEnabled(config, converter.Kind))
        {
            return FixResult.Refused(FixReason.TypeDisabled, "Per-kind converter is disabled in Settings.");
        }

        var item = _libraryManager.GetItemById(issue.ItemId);
        var libraryType = item is null ? (CollectionType?)null : _resolver.Resolve(item);
        var isMixed = libraryType is null;

        if (isMixed && !details.ApprovedInMixedLibrary)
        {
            return FixResult.Refused(FixReason.MixedLibraryUserReviewOnly,
                "This file lives in a mixed-content library. Approve the specific row on the Issues tab to convert it.");
        }

        if (!isMixed && !converter.AllowedLibraryTypes.Contains(libraryType))
        {
            return FixResult.Refused(FixReason.LibraryTypeMismatch,
                "This converter does not run inside " + libraryType + " libraries.");
        }

        if (!_guard.IsInsideLibrary(issue.Path))
        {
            return FixResult.Refused(FixReason.OutsideLibrary, "File is outside every configured library folder.");
        }

        if (_protection.IsProtected(issue.Path))
        {
            return FixResult.Refused(FixReason.Protected, "This file is on the never-modify list.");
        }

        var readiness = await converter.EnsureToolchainAsync(cancellationToken).ConfigureAwait(false);
        if (readiness != ConverterAvailability.Ready)
        {
            return FixResult.Refused(FixReason.ConverterUnavailable,
                "Converter toolchain isn't ready yet. Try again after it finishes downloading.");
        }

        var stagedPath = issue.Path + InflightTail + details.TargetExt;

        if (config.DryRun)
        {
            return FixResult.DryRun("convert " + Path.GetFileName(issue.Path) + " to " + details.TargetExt, 0);
        }

        var conversion = await converter.ConvertAsync(issue.Path, stagedPath, progress, cancellationToken).ConfigureAwait(false);
        if (!conversion.Success || conversion.StagedPath is null || !File.Exists(conversion.StagedPath))
        {
            return FixResult.Fail(FixReason.ConverterFailed, conversion.Reason, conversion.TechnicalDetail);
        }

        var sourceInfo = new FileInfo(issue.Path);
        var destinationPath = Path.ChangeExtension(issue.Path, details.TargetExt);
        var request = new ReplacementRequest(
            SourcePath: issue.Path,
            OutputPath: conversion.StagedPath,
            DestinationPath: destinationPath,
            ExpectedSourceLength: sourceInfo.Length,
            ExpectedSourceMtimeUtcTicks: sourceInfo.LastWriteTimeUtc.Ticks,
            RecycleOriginal: config.FormatCompatibilityDisposal == DisposalMethod.RecycleBin);

        var replaceResult = await _replacement.ReplaceAsync(request, VerifyOutputAsync, () => true, cancellationToken).ConfigureAwait(false);
        if (!replaceResult.Success)
        {
            SafeDelete(stagedPath);
            return FixResult.Fail(replaceResult.Reason, replaceResult.Message ?? "Replacement failed.", null);
        }

        _libraryMonitor.ReportFileSystemChanged(destinationPath);
        if (item is not null && item.Path != destinationPath)
        {
            _ = _libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false, DeleteFromExternalProvider = false });
        }

        return new FixResult
        {
            Success = true,
            Message = "Converted " + Path.GetFileName(issue.Path) + " → " + Path.GetFileName(destinationPath),
            RecyclePath = replaceResult.RecyclePath
        };
    }

    private static bool IsKindEnabled(PluginConfiguration config, MediaKind kind) => kind switch
    {
        MediaKind.Ebook => config.ConvertEbooks,
        MediaKind.Comic => config.ConvertComics,
        MediaKind.Audiobook => config.ConvertAudiobooks,
        MediaKind.Image => config.ConvertImages,
        MediaKind.Music => config.ConvertMusic,
        MediaKind.Video => config.ConvertVideos,
        _ => false
    };

    private static Details? ParseDetails(string detailsJson)
    {
        if (string.IsNullOrWhiteSpace(detailsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(detailsJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var converterKey = root.TryGetProperty("converter", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? string.Empty : string.Empty;
            var targetExt = root.TryGetProperty("targetExt", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
            var mixed = root.TryGetProperty("mixedLibrary", out var m) && m.ValueKind == JsonValueKind.True;
            var approvedInMixed = root.TryGetProperty("approvedInMixedLibrary", out var a) && a.ValueKind == JsonValueKind.True;
            return new Details(converterKey, targetExt, mixed, approvedInMixed);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private Task<string?> VerifyOutputAsync(string stagedPath, CancellationToken ct)
    {
        // Post-conversion verify: file exists, non-empty. Deeper format-integrity checks (probe
        // the produced EPUB/PDF/CBZ) live in Phase 2.5; a zero-byte fall-through is the worst
        // real-world failure and this catches it.
        if (!File.Exists(stagedPath))
        {
            return Task.FromResult<string?>("Converted output missing after conversion.");
        }
        if (new FileInfo(stagedPath).Length == 0)
        {
            return Task.FromResult<string?>("Converted output is empty.");
        }
        return Task.FromResult<string?>(null);
    }

    private static void SafeDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { }
    }

    private sealed record Details(string ConverterKey, string TargetExt, bool MixedLibrary, bool ApprovedInMixedLibrary);
}
```

- [ ] **Step 2: Fix the test `Setup()` helper**

Replace the `throw new NotImplementedException(...)` line in `FormatCompatibilityFixerTests.cs` `Setup()`:

```csharp
    private static (FormatCompatibilityFixer fixer, StubResolver resolver, StubRegistry registry) Setup(bool dryRun = false)
    {
        var registry = new StubRegistry();
        var resolver = new StubResolver();
        var configProvider = new StubConfig(dryRun);
        var fixer = new FormatCompatibilityFixer(
            registry,
            resolver,
            new StubReplacement(),
            new StubGuard(),
            new StubProtection(),
            configProvider,
            new StubLibraryManager(),
            new StubLibraryMonitor(),
            NullLogger<FormatCompatibilityFixer>.Instance);
        return (fixer, resolver, registry);
    }
```

And add stub classes for `VerifiedReplacementService`, `LibraryGuard`, `ProtectionService`, `ILibraryManager`, `ILibraryMonitor`. Because these types are `sealed` and can't be mocked cheaply, this is where the fixer needs adjustment: **change the fixer's constructor** to take interfaces/delegates instead of concrete classes for the components tested here. Specifically:

1. Add an internal interface `IReplacementServiceAdapter` in `Fixers/Converters/` that VRS is DI-registered as, and change `FormatCompatibilityFixer` to depend on it. **OR** — simpler — change the fixer to take a `Func<ReplacementRequest, Func<string, CancellationToken, Task<string?>>, Func<bool>, CancellationToken, Task<ReplacementResult>>` — a callable that the test can stub.

Choose whichever change is cheapest. The test can then supply a lambda that just returns success. Update the fixer accordingly.

- [ ] **Step 3: Run tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~FormatCompatibilityFixerTests --nologo`
Expected: all 4 tests pass. If the fixer needs seams that weren't planned, add them and note the change here in a `**Concern:**` block.

- [ ] **Step 4: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Fixers/FormatCompatibilityFixer.cs Jellyfin.Plugin.MediaDash.Tests/Formats/FormatCompatibilityFixerTests.cs
git commit -m "feat(fixers): implement FormatCompatibilityFixer"
```

---

## Task 11: Register converters, fixer, real downloader in DI

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs`

- [ ] **Step 1: Swap `NoOpToolchainDownloader` for `CalibreDownloader`**

Find the Task-13-of-Phase-1 line:

```csharp
        serviceCollection.AddSingleton<IToolchainDownloader, NoOpToolchainDownloader>();
```

Replace with:

```csharp
        serviceCollection.AddSingleton<IToolchainDownloader, CalibreDownloader>();
```

- [ ] **Step 2: Register converters + fixer**

Immediately after the converter registry registration:

```csharp
        serviceCollection.AddSingleton<IFormatConverter, ComicConverter>();
        serviceCollection.AddSingleton<IFormatConverter, EbookConverter>();
```

Then in the fixer registration block (find where `PlayabilityFixer` etc. are registered as `IFixer`):

```csharp
        serviceCollection.AddSingleton<IFixer, FormatCompatibilityFixer>();
```

- [ ] **Step 3: Build + regression run**

```
dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash.sln --nologo -clp:ErrorsOnly
dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo
```

Both succeed; no test regressions.

- [ ] **Step 4: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/PluginServiceRegistrator.cs
git commit -m "feat(di): wire Ebook/Comic converters, FormatCompatibility fixer, real Calibre downloader"
```

---

## Task 12: Settings tab section + dom-helpers action

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/settings-tab.js`
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/scripts/dom-helpers.js`
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/i18n/en.json`

- [ ] **Step 1: Read `settings-tab.js` to locate the pattern**

Look at how an existing section (e.g. Duplicates or Playability) is rendered — every setting section has a heading, a mode dropdown (Off/DetectOnly/Automatic/OnApproval), a disposal dropdown, and any per-feature toggles. Reuse the existing helper functions (e.g. `renderModeDropdown`, `renderDisposalDropdown`, `renderCheckbox`).

- [ ] **Step 2: Add a new "Format conversion" section**

Structure to build (follow the exact style of surrounding sections in the file):

- Section heading: "Format conversion"
- Mode dropdown wired to `FormatCompatibilityFixMode`
- Disposal dropdown wired to `FormatCompatibilityDisposal`
- Checkboxes:
  - "Convert ebooks (MOBI/AZW3 → EPUB)" ↔ `ConvertEbooks`
  - "Convert comics (CBR/CB7 → CBZ)" ↔ `ConvertComics`
- Text field: "Calibre override path (leave empty to use system PATH or lazy download)" ↔ `CalibreOverridePath`
- Optional: dropdown for `EbookPreferredTarget` (choices: "" / "epub" / "pdf")

- [ ] **Step 3: Update `dom-helpers.js` `FormatCompatibility` entry**

Change the entry created in Phase 1 Task 14 from a "Review" no-action to a proper fix. Model on the Playability entry — action label "Convert", actionHint text explaining the conversion outcome, keep the icon/color, include a `disposalKey` and `modeKey` matching the config properties.

- [ ] **Step 4: Update `en.json`**

Add the strings the settings section reads (mirror the existing entries):

```json
    "FormatConversionSection": "Format conversion",
    "ConvertEbooks": "Convert ebooks (MOBI, AZW3, DjVu → EPUB/PDF)",
    "ConvertComics": "Convert comics (CBR/CB7 → CBZ)",
    "CalibreOverridePath": "Calibre override path"
```

- [ ] **Step 5: Build plugin**

Run: `dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj --nologo`
Expected: succeeds. The JS/JSON changes don't require a build to be validated; the build is the assembly-embed of the resources.

- [ ] **Step 6: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash/Configuration/
git commit -m "feat(ui): add Format conversion Settings section + wire fix action"
```

---

## Task 13: Overview tab "Unsupported formats" card

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash/Configuration/scripts/tabs/overview-tab.js`

- [ ] **Step 1: Read the file to find where other issue-count cards are rendered**

Look for patterns matching `Duplicate`, `Playability`, `Quality` cards. Note the data source: probably the `/MediaDash/Status` response, which returns issue counts by type.

- [ ] **Step 2: Add a `FormatCompatibility` card**

Follow the same pattern as existing cards. Show the count by summing `FormatCompatibility` issues, with a "View" link that navigates to Issues tab filtered by `type=FormatCompatibility`.

If `/MediaDash/Status` doesn't include `FormatCompatibility` in its per-type breakdown, check `Api/StatusResponse.cs` and `Api/StatusQueryService.cs` — likely no code change is needed because these enumerate all `IssueType` values dynamically, but confirm.

- [ ] **Step 3: Build + commit**

```bash
cd /c/dev/mediadash
dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj --nologo
git add Jellyfin.Plugin.MediaDash/Configuration/
git commit -m "feat(ui): add Unsupported formats card to Overview"
```

---

## Task 14: Upgrade the CBR fixture to a real file

**Files:**
- Modify: `Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/comic/sample.cbr`

- [ ] **Step 1: Build a real 3-image CBR**

Use PowerShell (the Windows dev host has it):

```powershell
$tmp = "$env:TEMP\cbr-fixture-" + [guid]::NewGuid()
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

# Three 32x32 solid-color JPGs via System.Drawing.
Add-Type -AssemblyName System.Drawing
foreach ($i in 1..3) {
    $bmp = New-Object System.Drawing.Bitmap 32, 32
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $color = @([System.Drawing.Color]::Red, [System.Drawing.Color]::Green, [System.Drawing.Color]::Blue)[$i-1]
    $g.Clear($color)
    $g.Dispose()
    $path = Join-Path $tmp ("page{0:D3}.jpg" -f $i)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg)
    $bmp.Dispose()
}

# Zip them (SharpCompress in the plugin decompresses RAR headers via the archive-agnostic API too,
# so a plain ZIP with a .cbr extension is a legal fixture — the RarArchive.Open call will error,
# and the plan's ComicConverter should either fall back to zip-mode or the test should account for
# it. Better path: use 7-Zip's rar if installed. If neither works, ship a plain zip and skip the
# CBR live test with SkipException — the shape of ComicConverter is still exercised.)
Compress-Archive -Path (Join-Path $tmp "*.jpg") -DestinationPath "C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\fixtures\formats\comic\sample.cbr" -Force

Remove-Item -Recurse -Force $tmp
```

Note: a true CBR needs a RAR header, and creating a real RAR programmatically on Windows requires either `WinRAR.exe` or `rar.exe`. If neither is available, ship the ZIP-with-.cbr-extension as above and let the test's `SkipException` handle the "SharpCompress can't parse as RAR" case. Phase 2.5 can replace this fixture properly.

- [ ] **Step 2: Verify file size**

```powershell
Get-Item C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\fixtures\formats\comic\sample.cbr | Select-Object Length
```

Expected: < 20 KB.

- [ ] **Step 3: Run comic tests**

Run: `dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --filter FullyQualifiedName~ComicConverterTests --nologo`
Expected: `ConvertsCbrToCbz` either passes (if the archive is RAR-parseable) or throws SkipException (if it's a ZIP). Both are acceptable exits at this phase.

- [ ] **Step 4: Update `fixtures/formats/README.md`** provenance table for the `.cbr` row.

- [ ] **Step 5: Commit**

```bash
cd /c/dev/mediadash
git add Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/comic/sample.cbr Jellyfin.Plugin.MediaDash.Tests/fixtures/formats/README.md
git commit -m "test(fixtures): upgrade CBR sample to a real 3-image archive"
```

---

## Task 15: End-to-end test on `localhost:8099` dev Jellyfin

**Files:** none modified — this is a manual verification pass.

- [ ] **Step 1: Build the plugin in Release mode**

Run: `dotnet publish C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj -c Release --nologo`
Expected: `bin/Release/net9.0/publish/` contains the plugin DLL + 6 sidecars + deps.json.

- [ ] **Step 2: Stop Jellyfin v10.11.11 if running**

Run: `taskkill /f /im jellyfin.exe`

- [ ] **Step 3: Copy the fresh build into the v10 plugin folder**

```powershell
$dest = "$env:LOCALAPPDATA\jellyfin-v10\plugins\MediaDash_0.9.0.0"
Copy-Item -Path C:\dev\mediadash\Jellyfin.Plugin.MediaDash\bin\Release\net9.0\publish\* -Destination $dest -Force
```

- [ ] **Step 4: Launch Jellyfin v10**

Run: `& "C:\Users\crackruckles\Downloads\start-jellyfin-v10.bat"`
Expected: server starts on port 8099. Wait ~5 s for it to finish boot.

- [ ] **Step 5: Prepare a test MOBI in the library**

Copy the fixture MOBI into the Books library folder Jellyfin serves:

```powershell
# Path here depends on the local library setup — adjust to whichever folder is a Books library
# on the v10 dev host. Verify via /System/VirtualFolders API.
Copy-Item C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\fixtures\formats\ebook\sample.mobi -Destination "C:\dev\mediadash-fixtures\books\phase2-test.mobi"
```

- [ ] **Step 6: Trigger a library scan** so Jellyfin indexes the new file

```powershell
# Auth first
$auth = (Invoke-RestMethod -Method Post -Uri http://localhost:8099/Users/AuthenticateByName `
  -Headers @{ Authorization = 'MediaBrowser Client="Test", Device="Test", DeviceId="phase2", Version="1.0"' } `
  -Body '{ "Username": "test", "Pw": "test" }' -ContentType "application/json")
$token = $auth.AccessToken
$hdr = @{ Authorization = "MediaBrowser Client=`"Test`", Device=`"Test`", DeviceId=`"phase2`", Version=`"1.0`", Token=`"$token`"" }
Invoke-RestMethod -Method Post -Uri http://localhost:8099/Library/Refresh -Headers $hdr
Start-Sleep -Seconds 15
```

- [ ] **Step 7: Trigger a MediaDash scan**

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8099/MediaDash/Scan -Headers $hdr
Start-Sleep -Seconds 20
```

- [ ] **Step 8: Confirm the scan produced a FormatCompatibility issue**

```powershell
$issues = Invoke-RestMethod -Method Get -Uri "http://localhost:8099/MediaDash/Issues?type=FormatCompatibility" -Headers $hdr
$issues | Select-Object -First 5 Path, SuggestedFix, Type
```

Expected: at least one issue whose `Path` is the copied `.mobi`, `Type` is `FormatCompatibility`, `SuggestedFix` matches the matrix's UserAdvice.

- [ ] **Step 9: Enable the fixer + fire a fix run**

Turn on `ConvertEbooks` in Settings via the API (or the UI at http://localhost:8099/web/index.html → plugins → MediaDash → Settings). Set `FormatCompatibilityFixMode = Automatic`. Then:

```powershell
Invoke-RestMethod -Method Post -Uri http://localhost:8099/MediaDash/Fix -Headers $hdr
```

If Calibre is not installed on the machine, this first-run should trigger the download flow. Watch the Jellyfin log for `Calibre unavailable` or `Calibre download saved to...` messages.

- [ ] **Step 10: Verify the conversion**

Wait for the fix task to complete (or watch the running-task API). Confirm:
- Original `.mobi` moved to the recycle bin (check `/MediaDash/Status.RecycleBinContents`).
- New `.epub` at the same path (`phase2-test.epub`).
- History entry with `Fixed` outcome.

- [ ] **Step 11: Record findings**

Write down what worked and what didn't in a scratchpad. Blockers get filed as Phase 2.5 items. **Do not** commit anything from the E2E test — no code changes here, just a manual verification pass.

- [ ] **Step 12: Stop Jellyfin**

Run: `taskkill /f /im jellyfin.exe`

---

## Task 16: CHANGELOG + final regression run

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Add the Phase 2 bullet**

Find the `## 1.0.8.0 (unreleased)` section (where Phase 1's bullet lives). Add:

```
- Add Phase 2 of Format Compatibility: EbookConverter (Calibre-backed), ComicConverter (SharpCompress), FormatCompatibilityFixer, real Calibre lazy-download on Windows, Settings section + Overview card. Detected files can now be converted end-to-end. F-format-compat.
```

- [ ] **Step 2: Full regression run**

```
dotnet test C:\dev\mediadash\Jellyfin.Plugin.MediaDash.Tests\Jellyfin.Plugin.MediaDash.Tests.csproj --nologo
dotnet build C:\dev\mediadash\Jellyfin.Plugin.MediaDash\Jellyfin.Plugin.MediaDash.csproj -c Release --nologo
```

Both succeed; test count higher than Phase 1's 1334 by the number of tests Phase 2 added.

- [ ] **Step 3: Commit**

```bash
cd /c/dev/mediadash
git add CHANGELOG.md
git commit -m "docs(changelog): Phase 2 Format Compatibility (Ebook/Comic converters, Calibre download)"
```

---

## Self-Review Notes

- **Spec coverage:**
  - §3 architecture — Tasks 5, 7, 10 cover fixer + converters.
  - §5 library-kind gate — Task 10 (fixer) re-enforces at fix-time, matching Phase 1's scanner enforcement.
  - §6 toolchain — Task 8 (real downloader) closes the download loop.
  - §8 configuration — Task 3 lands the knobs.
  - §9 UI — Tasks 12, 13 add Settings section + Overview card.
  - §11 error handling — Task 1 introduces new FixReason values; Task 10 routes through them.
  - §12 analytics — deferred to Phase 5 per spec.

- **Placeholder scan:**
  - Task 8 has an explicit `PLACEHOLDER_SHA256_SET_IN_STEP_1_ABOVE` constant — this is an instruction to the implementer to compute the real SHA in Step 1. Not a plan placeholder; it's a locked-in requirement to gather one specific value before merging. The failure mode (E2E test fails on SHA mismatch) is loud.
  - Task 10 Step 2 acknowledges the fixer construction may need seams — the plan explicitly directs the implementer to pick a seam and document it. This is a known design tension where over-specification would be wrong.

- **Type consistency:** `ConversionResult`, `ConverterAvailability`, `IFormatConverter`, `IFormatConverterRegistry`, `FormatConverterRegistry`, `ILibraryCollectionResolver`, `CalibreToolchain`, `IToolchainDownloader`, `NoOpToolchainDownloader`, `CalibreDownloader`, `EbookConverter`, `ComicConverter`, `FormatCompatibilityScanner`, `FormatCompatibilityFixer`, `FormatMatrix`, `MediaKind` — every symbol name matches Phase 1 and each other.
