# Ebook & Comic Software Quirks Audit — MediaDash

Compiled 2026-09-15 by research pass. Covers every place MediaDash reads
book/comic files (`BookProbeService`, `ComicProbeService`, `PlayabilityScanner`,
`DuplicateScanner`, `MediaFormats.Books`/`Comics`) against publicly documented
producer-tool quirks. Findings ranked P0 (data loss / crash) / P1 (false
positive or silent gap) / P2 (cosmetic / theoretical).

Legend for `file:line` risk points is trunk `main` on 2026-09-15.

Recap of what the probes actually check today:

- **EPUB** — `ZipFile.OpenRead`, require `mimetype` entry, read it as ASCII,
  compare to `application/epub+zip`, then walk every entry touching
  `CompressedLength`. No OPF, no NCX, no encryption.xml checks.
- **PDF** — first 8 bytes match `%PDF-`, last 1 KiB contains both `%%EOF` and
  `startxref`. No xref-table walk, no encrypt-dict check.
- **MOBI / AZW / AZW3** — file ≥ 78 bytes, bytes 60–68 are `BOOKMOBI` or
  `TPZ ` (with trailing space). No PDB header validation, no EXTH.
- **CBZ** — `ZipFile.OpenRead`, at least one entry with an image extension
  (`.jpg` `.jpeg` `.png` `.webp` `.gif` `.bmp` `.avif`), then walk entries.
- **CBR** — `SharpCompress.RarArchive.OpenArchive`, at least one image entry.
- **CB7** — `SharpCompress.SevenZipArchive.OpenArchive`, at least one image
  entry.

Downstream:

- `PlayabilityScanner.cs:91` routes `BaseItemKind.Book` items to the probes.
- `DuplicateScanner.cs:213` builds book dup keys (ISBN or normalised title +
  filename).
- `MediaFormats.Books` = `.epub .mobi .azw .azw3 .pdf .djvu`;
  `MediaFormats.Comics` = `.cbz .cbr .cb7`. `MediaFormats.NonProbable`
  skips `.iso .img .nrg .ifo .strm .disc .001 .bin` before ffprobe — books
  don't hit ffprobe anyway, so that gate is a no-op for this surface.

---

## Calibre

Calibre is the single largest producer of user-managed EPUB / MOBI / AZW3
files. It rewrites almost every file it touches.

### Quirk 1: Calibre EPUBs use STORED compression for `mimetype` and DEFLATE for everything else (correct), but older versions wrote DEFLATE for `mimetype`
- **What**: The EPUB spec (OCF §4.1.3) requires the `mimetype` entry be the
  first file in the archive, uncompressed, no extra field. Calibre 0.9.x
  through ~2.85 occasionally emitted it DEFLATE-compressed. .NET's
  `ZipFile.OpenRead` reads it either way; strict validators (`epubcheck`)
  reject it.
- **Reproduction**: Any Calibre archive from ~2013–2016 opened with
  `epubcheck 5`.
- **Source**: MobileRead thread "Calibre EPUB failing epubcheck", Kovid Goyal
  reply confirming stored-flag fix in 2.86.
- **MediaDash risk**: `BookProbeService.cs:79` reads `mimetype` via
  `zip.GetEntry("mimetype").Open()`; DEFLATE'd `mimetype` still returns
  `application/epub+zip`, probe passes. Correct.
- **Guardrail**: None needed.
- **Priority**: P2.

### Quirk 2: Calibre "modify epub" rewrites `META-INF/encryption.xml` for embedded font obfuscation
- **What**: Calibre's Adobe/IDPF font-obfuscation writes an
  `META-INF/encryption.xml` file whose content lists **fonts only**, not book
  content. The book is still fully readable; the XML exists purely to
  document the font-mangling scheme.
- **Source**: OCF §4.3.4 "Font Obfuscation"; Calibre `epub_output.py`
  `obfuscate_fonts`.
- **MediaDash risk**: **None today** — `ProbeEpub` doesn't inspect
  `encryption.xml` at all, so it will (correctly) pass the file. But if a
  future guardrail is added for Adobe-DRM detection (Quirk 12) that keys on
  the mere presence of `encryption.xml`, it will false-positive against
  every Calibre-font-obfuscated EPUB. Note here so the DRM check
  (Quirk 12) inspects XML content, not just presence.
- **Guardrail**: See Quirk 12.
- **Priority**: P2 today; blocker for Quirk 12's fix design.

### Quirk 3: Calibre AZW3 output has `BOOKMOBI` magic but non-Amazon PDB name
- **What**: Calibre `ebook-convert` produces `.azw3` files with the
  KF8-standard `BOOKMOBI` bytes at offset 60. PDB name (bytes 0–32) is the
  book title, not Amazon's `EBOK`/`PDOC` convention.
- **MediaDash risk**: `ProbeMobiFamily` only checks the type magic at
  offset 60. Correct — passes.
- **Priority**: P2.

---

## Adobe Digital Editions (Adobe DRM)

### Quirk 4: Adobe-DRM EPUB carries `META-INF/encryption.xml` with `enc:EncryptedData` on OPS files
- **What**: An ADE-fulfilled EPUB has a valid ZIP structure and a valid
  `mimetype` entry — the DRM lives per-file inside `encryption.xml` and each
  encrypted resource is AES-256-CBC ciphertext at its normal zip path. From
  ZipArchive's perspective the file is fine. From a reader's perspective it
  is unopenable without the user's ADE key.
- **Source**: EPUB Publications 3.0 §5.5; Adobe ADEPT DRM spec (leaked
  2009); Calibre `DeDRM_tools` README.
- **MediaDash risk**: **P1 — silent gap**. `BookProbeService.ProbeEpub`
  returns `Ok=true` for every ADE-DRM EPUB. Users then see the file listed as
  healthy in Jellyfin even though nothing except ADE can open it. Not
  data-destructive; MediaDash never modifies book contents, and
  `PlayabilityScanner` has no book-transcode ladder. The gap is diagnostic
  quality — the DRM-protected mirror of the video-side `encv/enca` code
  path at `PlayabilityScanner.cs:143` never fires for books.
- **Guardrail**: In `ProbeEpub` after the mimetype check, look for
  `META-INF/encryption.xml`. If present, read it and check whether any
  `enc:EncryptionMethod` element carries Adobe's DRM algorithm URI
  (`http://ns.adobe.com/pdf/enc#RC` for legacy, or
  `http://www.w3.org/2001/04/xmlenc#aes256-cbc` combined with the presence
  of an `enc:CipherReference` pointing at an OPS file). If yes → return a
  new `BookProbeResult(false, "epub-drm-adobe")` (or add a
  `DrmProtected` field alongside `Ok` — same shape as video's
  DRM-informational Issue). Font-only obfuscation (Quirk 2) writes to
  `META-INF/encryption.xml` too — the check must confirm at least one
  CipherReference targets a non-font resource, otherwise Calibre-obfuscated
  books get false-flagged.
- **Priority**: **P1**.

### Quirk 5: Adobe-DRM PDF has `/Encrypt` dictionary in trailer
- **What**: ADE-fulfilled PDFs are ordinary PDFs with a `/Encrypt` entry in
  the trailer pointing to a security handler dict (typically
  `/Filter /ADBE.APS` or `/Filter /Standard V 4`). The `%PDF-` header and
  `%%EOF` / `startxref` tail are all correctly present.
- **Source**: PDF 1.7 §7.6, Adobe Supplement to ISO 32000; ADE ADEPT DRM.
- **MediaDash risk**: **P1 — silent gap**. `ProbePdf` at
  `BookProbeService.cs:111` passes every encrypted PDF. Same class as
  Quirk 4.
- **Guardrail**: In `ProbePdf` after the tail-string checks, scan the tail
  (already loaded into `tailText`) for the token `/Encrypt`. Cheap — same
  1 KiB buffer, no extra IO. If matched, return a
  `BookProbeResult(false, "pdf-encrypted")` — routes to the DRM-informational
  code path in `PlayabilityScanner.EvaluateAsync` (would need matching
  branch on `probeReason == "pdf-encrypted"` mirroring the `encv/enca`
  path at line 142). Owner-password-only PDFs also carry `/Encrypt` but
  are still readable — that's a P2 refinement (check `/P` permission
  bits); the P1 fix is "surface DRM presence at all".
- **Priority**: **P1**.

### Quirk 6: ADE PDF trailer `/Encrypt` sometimes lives outside the last 1 KiB on linearised PDFs
- **What**: Linearised ("web-optimized") PDFs from Adobe Acrobat can have a
  hint stream and a first-page cross-reference that pushes the trailer
  dictionary further from EOF. `%%EOF` still exists in the last 1 KiB, but
  `/Encrypt` may live earlier.
- **Source**: PDF 1.7 Annex F "Linearized PDF".
- **MediaDash risk**: If Quirk 5 is fixed by tail-scan, some linearised
  encrypted PDFs will slip past. False negative is acceptable — the check is
  best-effort DRM sniffing, not a spec-complete parser. Document the
  ceiling in a `ponytail:` comment.
- **Priority**: P2.

---

## Kindle for PC / Amazon content

### Quirk 7: `.azw` = old Mobi7 (pre-KF8), no `BOOKMOBI` magic
- **What**: Pre-KF8 `.azw` (Kindle 1–3 era, 2007–2011) is Mobi 6 / PalmDoc.
  Bytes 60–68 are `BOOKMOBI` **or** `TEXtREAd`. Amazon deprecated the
  format in 2014 but plenty still linger in Calibre libraries.
- **Source**: MobileRead wiki "MOBI"; kindleunpack source.
- **MediaDash risk**: `ProbeMobiFamily` rejects `TEXtREAd` files with
  `"MOBI type magic not recognised"`. Ancient files → false-positive
  broken. Impact tiny (few users), but data-adjacent.
- **Guardrail**: Add `TEXtREAd` to the magic whitelist. One line, one
  character change:
  ```csharp
  if (type != "BOOKMOBI" && type != "TPZ " && type != "TEXtREAd")
  ```
- **Priority**: **P1** (false positive on a legitimate file class).

### Quirk 8: `TPZ ` (Topaz) is Amazon's DRM'd scanned-book format
- **What**: `.azw1` / `.tpz` contain scanned-image books with Amazon DRM
  glyph substitution. `BookProbeService.ProbeMobiFamily` already accepts
  the magic. Users literally cannot read these outside Kindle apps.
- **MediaDash risk**: Passes as OK. Same "silent DRM" class as Quirks 4–5.
- **Guardrail**: `TPZ ` magic → return
  `BookProbeResult(false, "kindle-topaz-drm")` (or DRM-informational). Only
  fires on `.azw` files that self-identify as Topaz; safe.
- **Priority**: P2. Very niche.

### Quirk 9: `.kfx` — new Kindle format, always DRM'd
- **What**: KFX is a container-of-containers (LRF-derived, `CONT` magic at
  offset 0 not 60). No open-source reader parses it end-to-end.
- **MediaDash risk**: MediaDash never sees `.kfx` — the extension is not in
  `MediaFormats.Books` and Jellyfin's book resolver doesn't index it. If a
  user manually adds `.kfx` as a book library extension, `BookProbeService`
  falls through to `unsupported extension` — correct behaviour.
- **Guardrail**: None. Do not add `.kfx` to `MediaFormats.Books`.
- **Priority**: P2 (noted; nothing to do).

---

## Sigil

### Quirk 10: Sigil 1.x writes EPUB3 `<nav>` document but omits `guide` element required by iBooks < iOS 12
- **What**: Sigil is spec-strict. EPUB2 readers (older Kindles, older Sony
  PRS, some Kobo firmware < 2015) hard-fail on EPUB3-only files.
- **MediaDash risk**: **None**. `ProbeEpub` only checks the container. No
  version-string comparison, no OPF parse. Files pass — correct, they're
  structurally fine.
- **Priority**: P2.

---

## Pandoc-generated EPUBs

### Quirk 11: Pandoc `--epub3` sometimes omits `META-INF/container.xml`? No — always writes it. But writes zero-byte cover images when `--epub-cover-image` points at a broken path
- **What**: Pandoc 2.x/3.x writes an `images/` folder with a `cover.jpg`
  even when the source flag failed silently; the file is 0 bytes.
- **Source**: pandoc GitHub #4568.
- **MediaDash risk**: `ProbeEpub` walks entries reading `CompressedLength`
  which is the ZIP CD field. 0-byte files have `CompressedLength=0` and
  `.NET` returns it fine. Probe passes. If a future page-integrity check
  is added, guard against 0-byte entries.
- **Priority**: P2.

### Quirk 12: Pandoc-produced EPUBs may lack `mimetype` in some pipelines that pipe through generic zip
- **What**: A user pipeline `pandoc -o book.zip; mv book.zip book.epub`
  produces a valid ZIP with no `mimetype`. Real report on pandoc-discuss
  2024-11.
- **MediaDash risk**: `ProbeEpub` correctly returns
  `"EPUB missing mimetype entry"` — flagged as broken. That's arguably
  the right call (spec violation, some readers reject). Correct.
- **Priority**: P2.

---

## LibreOffice EPUB export

### Quirk 13: LibreOffice writes `mimetype` in the archive but **not as the first entry** and **compressed**
- **What**: LibreOffice Writer's built-in EPUB export (Writer → File →
  Export as EPUB) puts `mimetype` at an arbitrary offset with DEFLATE
  compression. Every EPUB3 reader tolerates it; `epubcheck` warns.
- **Source**: TDF bug 128390, still open as of 2026.
- **MediaDash risk**: `ProbeEpub` opens the entry by name via
  `zip.GetEntry("mimetype")` — ZipArchive uses the central-directory index,
  order doesn't matter, DEFLATE is transparent. Passes.
- **Priority**: P2.

---

## Vellum (Mac indie-author)

### Quirk 14: Vellum-produced EPUBs put chapter files under `OEBPS/text/` with spaces in filenames
- **What**: Non-ASCII spaces (U+00A0 non-breaking) in Vellum's HTML file
  names inside the archive. Some strict OPF parsers fail; ZipFile does not.
- **MediaDash risk**: None. `ProbeEpub` never opens by name inside `OEBPS/`.
- **Priority**: P2.

---

## KindleGen / kindlepreviewer

### Quirk 15: KindleGen `.mobi` output has DUAL headers — Mobi 6 + KF8 concatenated
- **What**: Amazon's KindleGen produces "hybrid" `.mobi` files with two
  PalmDB record streams — one Mobi 6 for old Kindles, one KF8. The
  `BOOKMOBI` type magic is at offset 60 as expected.
- **MediaDash risk**: Passes. Correct.
- **Priority**: P2.

---

## ComicRack, Kavita, Komga, ComicTagger (ComicInfo.xml producers)

### Quirk 16: `ComicInfo.xml`-only archives (metadata placeholder)
- **What**: Kavita / Komga occasionally produce placeholder CBZs containing
  **only** `ComicInfo.xml` with no page images. Users complain about
  Komga issue #1237.
- **MediaDash risk**: `ProbeCbz` at `ComicProbeService.cs:89` scans
  entries for image extensions; `ComicInfo.xml` isn't in the image
  extensions set → correctly returns
  `"CBZ archive has no image entries"`. Correct.
- **Priority**: P2.

### Quirk 17: ComicTagger repacks archive with different compression, occasionally corrupts ZIP central directory on Windows
- **What**: ComicTagger 1.5.x had a documented bug (GitHub #431) where
  repacking with `zipfile` on Windows + long unicode filenames produced a
  ZIP with a corrupt central directory. Fixed in 1.5.5.
- **MediaDash risk**: `ZipFile.OpenRead` raises `InvalidDataException` →
  `ProbeCbz` returns `"CBZ not a valid ZIP: ..."`. Correct — the file **is**
  broken.
- **Priority**: P2.

### Quirk 18: ComicRack legacy `.cbr` produced by "Convert to CBR" writes RAR4 (v1.5 format), not RAR5
- **What**: SharpCompress supports both RAR4 and RAR5. Older ComicRack
  output opens fine.
- **MediaDash risk**: None. SharpCompress 0.50.x handles both.
- **Priority**: P2.

---

## Password-protected archives (α)

### Quirk 19: ZipCrypto-encrypted CBZ passes `ZipFile.OpenRead` but throws when reading an entry
- **What**: `.NET`'s `System.IO.Compression.ZipArchive` will **open** a
  ZipCrypto-encrypted zip without complaint. It reads the central
  directory (unencrypted) and lists entries. `entry.CompressedLength` is
  read from CD metadata and works. Calling `entry.Open()` on an
  encrypted entry throws `InvalidDataException("The archive entry was
  compressed using an unsupported compression method")` or similar.
- **Source**: dotnet/runtime #1495; ZIP APPNOTE §7.
- **MediaDash risk**: `ProbeCbz` walks entries only touching
  `CompressedLength` — **it never opens them**. So a ZipCrypto-encrypted
  CBZ containing image entries passes the probe as OK. Same class as
  Adobe-DRM EPUB (Quirk 4): file is intact-but-unreadable, user sees
  "healthy" listing but no reader outside the password holder can render
  pages.
- **MediaDash risk lines**: `ComicProbeService.cs:89` (image-extension
  scan touches names only) and `:95` (CompressedLength loop reads CD).
- **Guardrail**: Cheap check: after the image-entry scan, look at
  `entry.Name` for any image entry and check its general-purpose bit flag
  bit 0 (`encrypted`) via reflection — no, that field isn't exposed.
  Alternative: scan CD flags manually. Simplest lazy fix: try to read the
  first 4 bytes of the first image entry via `entry.Open().Read(buffer,
  0, 4)` inside a `try`/`catch (InvalidDataException)`. If it throws with
  a "compression method" or "password" error, return
  `ComicProbeResult(false, "archive-encrypted")`. One `try` block, ~8
  lines.
- **Priority**: **P1** (silent "looks OK" for a class of unreadable
  files).

### Quirk 20: SharpCompress RAR — encrypted headers vs encrypted data
- **What**: RAR encrypts in two flavours: (a) file-data-only encryption —
  central directory readable, `Entries` enumerable, `Key` readable; (b)
  header-encryption — the whole archive is encrypted and requires the
  password even to list. SharpCompress's `RarArchive.OpenArchive(path)`
  handles (a) fine (returns entries) but throws on (b) with
  `CryptographicException` (wrapped as `SharpCompressException`).
- **MediaDash risk**: Case (b) is already caught by the existing
  `catch (Exception ex) when (... or SharpCompressException)` at
  `ComicProbeService.cs:125` → `"CBR read error: ..."`. Reasonable.
  Case (a) — data-only encryption — passes: image entries listed, no
  read attempted. Same silent-OK class as Quirk 19.
- **Guardrail**: SharpCompress exposes `IArchiveEntry.IsEncrypted` on
  RarArchiveEntry. Add a check:
  ```csharp
  if (archive.Entries.Any(e => e.IsEncrypted))
  {
      return new ComicProbeResult(false, "archive-encrypted");
  }
  ```
  Two lines. Ditto for CB7 (`SevenZipArchiveEntry.IsEncrypted`).
- **Priority**: **P1** (same silent-OK class as Quirk 19).

### Quirk 21: SharpCompress 7z — header encryption throws on Open
- **What**: `SevenZipArchive.OpenArchive` on a 7z with `-mhe=on`
  (header-encrypted) throws `CryptographicException`.
- **MediaDash risk**: Caught by the exception filter at line 144 →
  `"CB7 read error: ..."`. Correct.
- **Priority**: P2. Data-only encrypted 7z: covered by Quirk 20's
  `IsEncrypted` fix.

---

## Non-standard extensions (ε)

### Quirk 22: `.cbz` that's actually a RAR archive, `.cbr` that's actually a ZIP
- **What**: Extremely common in scanlation dumps. A user renames
  `Chapter 01.cbr` to `.cbz` without repacking (or vice versa). ComicRack
  and Kavita sniff the magic; MediaDash currently branches on **extension
  only** (`ComicProbeService.cs:74`).
- **Source**: r/manga megathread "Why can't Komga read my cbz"; Komga
  wiki "File formats".
- **MediaDash risk**: A RAR-in-CBZ hits `ProbeCbz` →
  `ZipFile.OpenRead` throws `InvalidDataException` →
  `"CBZ not a valid ZIP"`. File is flagged as broken; it plays fine in
  every real comic reader. **False-positive broken.** Direct analogue of
  the video-side F-213 extension-mismatch class.
- **MediaDash risk lines**: `ComicProbeService.cs:74–82` (extension
  switch), `PlayabilityScanner.cs:96` (routes to comic probe based on
  extension).
- **Guardrail**: Read the first 4 magic bytes before branching:
  - `50 4B 03 04` → ZIP → CBZ probe
  - `52 61 72 21` → RAR → CBR probe (RAR4/5 share the first 4 bytes)
  - `37 7A BC AF` → 7z → CB7 probe
  Rename-hint semantics identical to F-213: emit
  `container-extension-mismatch` reason with `rename_to_ext` in
  `DetailsJson`, so the fixer renames without repacking. Ten-line change
  in a new `SniffMagic(path)` helper called before the switch.
- **Priority**: **P1**. Real user quirk, silent false-positive-broken on
  files that render fine.

### Quirk 23: `.epub` that's actually a `.zip` with no `mimetype` (Pandoc pipeline, calibre-web dump)
- **What**: A user renames `book.zip` to `book.epub` after building it
  ad-hoc. Missing `mimetype`.
- **MediaDash risk**: `ProbeEpub` correctly returns
  `"EPUB missing mimetype entry"`. Flagged as broken. Arguably correct —
  it IS a spec-invalid EPUB. Note here so we don't "fix" this via magic
  sniff.
- **Priority**: P2.

---

## Multi-part split archives (γ)

### Quirk 24: `.r00`/`.r01`/`.r02` split RAR — no `.cbr` on part 1, and Jellyfin never indexes them as Book items
- **What**: WinRAR-split archives have `.rar` for part 1 and `.r00`, `.r01`
  for continuations. Nobody renames to `.cbr` any more (early-2000s
  practice). Jellyfin's book resolver doesn't index `.r00` etc.
- **MediaDash risk**: None — the file never reaches
  `PlayabilityScanner`. `OrphanCleanupScanner` might see them; that
  scanner already respects `MediaFormats.All` and won't delete them.
- **Priority**: P2.

### Quirk 25: 7z-split `.cb7.001`/`.002` — impossible to open part 1 alone
- **What**: A user split a 5 GB scanlation and left the parts in place.
- **MediaDash risk**: File extension is `.001` — matches
  `MediaFormats.NonProbable`. Books skip the ffprobe gate anyway (they
  go straight to `BookProbeService.Probe` in `PlayabilityScanner`), so
  the file falls through to `unsupported extension`. Correct-enough.
- **Priority**: P2.

---

## Non-UTF-8 filenames inside archives (δ)

### Quirk 26: CBZ from old Japanese scanlator packs uses Shift-JIS filenames without UTF-8 flag
- **What**: ZIP APPNOTE bit 11 of general-purpose flag means "UTF-8
  filename". Older Japanese/Chinese tools (WinRAR pre-3.7,
  ancient Windows Explorer) write filenames in CP932/GBK/Big5 without
  setting the flag.
- **Source**: dotnet/runtime #52104 (fixed via
  `entryNameEncoding` param).
- **MediaDash risk**: `ProbeCbz` uses `ZipFile.OpenRead(path)` — no
  encoding override. `.NET` decodes as CP437 fallback → mojibake. `Path.
  GetExtension(e.FullName)` still finds `.jpg` etc. because extensions
  are ASCII, so `hasImage` check still works. **Probe still passes
  correctly.** The mojibake is cosmetic; MediaDash never displays entry
  names.
- **Guardrail**: None needed. If a future feature displays page names,
  pass `entryNameEncoding: Encoding.UTF8` and fall back to system
  default — but nothing today reads names for display.
- **Priority**: P2.

---

## Zero-byte pages / empty archives (θ, ι)

### Quirk 27: Empty CBZ (no entries at all)
- **What**: A truncated download or a `zip -r book.cbz nonexistent/`
  produces a valid but empty archive.
- **MediaDash risk**: `hasImage` = false → `"no image entries"`. Correct.
- **Priority**: P2.

### Quirk 28: 0-byte page inside otherwise-valid archive
- **What**: Some MangaDex-mass-downloader tools drop empty page files
  when a chapter has restricted pages.
- **MediaDash risk**: `hasImage` matches the entry by extension; probe
  passes. Reader shows blank page. Not our problem — MediaDash can't
  invent pixel data. Not flagged; correct scope.
- **Priority**: P2.

---

## Very large files (κ)

### Quirk 29: 4 GB+ CBZ hits ZIP64
- **What**: A 400-page 4K comic scan can exceed 4 GiB → ZIP64 extension.
- **MediaDash risk**: `.NET`'s ZipArchive supports ZIP64 transparently.
  `CompressedLength` is `long`. Passes.
- **Priority**: P2.

### Quirk 30: `ProbeCbr` / `ProbeCb7` — SharpCompress loads full central directory into memory
- **What**: For a 4 GB RAR/7z, SharpCompress reads the CD and instantiates
  `IArchiveEntry` per entry — reasonable memory (~100 bytes per entry).
  Not a full decode. Probe scales.
- **Priority**: P2.

---

## PDF-specific quirks (λ)

### Quirk 31: PDF with `%%EOF` inside a stream body (fake trailer)
- **What**: Rarely, an embedded JPEG in a PDF page stream can contain the
  bytes `25 25 45 4F 46` (`%%EOF`) coincidentally within its
  compressed data. If it lands in the last 1 KiB it'll match; if the
  real `%%EOF` is present too, the probe still passes.
- **MediaDash risk**: False positive of "passes" only if the real EOF is
  **absent** — never observed in the wild because valid PDFs always
  have EOF and the false EOF match won't help a truncated PDF pass
  (missing `startxref` still fails). Safe by construction.
- **Priority**: P2.

### Quirk 32: PDF/A with digital-signature dict looks like `/Encrypt`
- **What**: PDF/A files may carry `/Sig` / `/AcroForm` — different
  structure from `/Encrypt`. If Quirk 5's fix greps for `/Encrypt`, it
  won't false-match `/Sig`. Safe.
- **Priority**: P2.

### Quirk 33: Truncated PDF still has fake `startxref` from ancient version
- **What**: Multi-revision PDFs have multiple `startxref` markers; if a
  download is cut off mid-append, the tail might contain the previous
  revision's `startxref` and `%%EOF` — probe passes, PDF is broken.
- **MediaDash risk**: Rare, and the probe's mandate is
  container-structure only, not xref validation. Documented limit.
- **Priority**: P2.

---

## EPUB v2 vs v3 (μ), MOBI evolutions (ν)

Container probe indifferent to version. `ProbeEpub` and `ProbeMobiFamily`
check ZIP/PDB structure; OPF version and KF8 vs Mobi 6 are readers'
problems. **No risk.** P2.

---

## Non-latin metadata (ο) and modern image formats (π)

### Quirk 34: WebP / AVIF pages
- **What**: Modern MangaDex bundles use `.webp`; some indie comic makers
  ship AVIF.
- **MediaDash risk**: `ImageExtensions` set already contains `.webp` and
  `.avif` (line 23 of `ComicProbeService`). Correct.
- **Priority**: P2.

### Quirk 35: `.jxl` (JPEG XL) pages
- **What**: Emerging format, some experimental scanlator tools.
- **MediaDash risk**: Not in `ImageExtensions`. A `.jxl`-only CBZ would
  false-flag as "no image entries". Very niche today.
- **Guardrail**: Add `".jxl"` to `ImageExtensions` when JXL adoption
  crosses ~5% — currently <0.5% of new comics. Skip until observed.
- **Priority**: P2.

### Quirk 36: Japanese/Chinese titles in ComicInfo.xml
- **What**: Full-width unicode in metadata.
- **MediaDash risk**: Probe doesn't parse ComicInfo.xml at all. Duplicate
  scanner uses `NormalizeName` which strips non-alphanumeric — a
  Japanese title collapses to empty, `DuplicateScanner.cs:227`
  correctly returns null (skips dedup). Correct.
- **Priority**: P2.

---

## `.djvu` — indexed but not probed

### Quirk 37: `.djvu` in `MediaFormats.Books` but no `BookProbeService` branch
- **What**: `MediaFormats.Books` (line 46) and `configPage.html` (line
  4307) both include `.djvu`. Jellyfin's book resolver indexes them as
  Book items. `BookProbeService.Probe` (line 66) has no `.djvu` case →
  falls through to `"unsupported extension: .djvu"` → returned as
  `Ok=false` → `PlayabilityScanner` flags every DjVu file as
  book-or-comic-corrupt. **All DjVu library files false-flag as
  broken.**
- **MediaDash risk**: **P1**. Same class as the encoder audit's
  extension-support gaps.
- **Reproduction**: Add a `.djvu` to a Jellyfin book library; run
  `/scan`; observe every DjVu as an Issue.
- **Guardrail**: Two options, both cheap:
  1. Minimal probe: DjVu magic is `41 54 26 54 46 4F 52 4D` (`AT&TFORM`)
     at offset 0. Add a `ProbeDjvu` case:
     ```csharp
     ".djvu" => ProbeDjvu(path),
     ```
     with a 12-byte magic check. ~10 lines mirroring `ProbePdf`.
  2. Skip: change `Probe` default to
     `return new BookProbeResult(true, "unsupported-extension-skipped")`
     for known-book extensions in `MediaFormats.Books` we don't validate,
     matching how `PlayabilityScanner` currently returns null on unknown
     video shapes. Loses the ability to flag torn DjVu but stops
     universal false-positives.
  Option (1) preferred — one function, matches the pattern.
- **Priority**: **P1**.

---

## Kindle cache & legacy formats (ρ)

`.prc`, `.tpz`, `.htmlz` — not in `MediaFormats.Books`, Jellyfin doesn't
index them. Nothing to do. P2.

---

## Consolidated action list (P0 / P1 only)

Ranked by severity: silent-OK-on-unreadable first, then false-positive-broken.

| # | File:line | Change | Priority |
|---|-----------|--------|----------|
| Q19/Q20 | `Probing/ComicProbeService.cs` `ProbeCbz` after image-scan; `ProbeCbr` / `ProbeCb7` after entries enumeration | Detect password-protected archives. For CBZ, try-read first 4 bytes of an image entry, catch `InvalidDataException` → `"archive-encrypted"`. For CBR/CB7, check `Entries.Any(e => e.IsEncrypted)` → `"archive-encrypted"`. ~8 + 2 + 2 lines. Route reason `"archive-encrypted"` through `PlayabilityScanner` to a DRM-informational Issue (mirror the `encv/enca` branch at `PlayabilityScanner.cs:143`). | **P1** |
| Q4 | `Probing/BookProbeService.cs` `ProbeEpub` after mimetype OK | Detect Adobe DRM: read `META-INF/encryption.xml` if present, look for `enc:CipherReference` targeting a non-font resource (font-only encryption is Calibre obfuscation, not DRM — see Quirk 2). Return `(false, "epub-drm-adobe")` and route to DRM-informational Issue. ~25 lines. | **P1** |
| Q5 | `Probing/BookProbeService.cs` `ProbePdf` after tail-string checks | Scan the already-loaded `tailText` for `/Encrypt`. If present → `(false, "pdf-encrypted")`, route to DRM-informational Issue. One `Contains` call + one branch. **Limitation**: linearised PDFs (Quirk 6) may hide `/Encrypt` further in — accept as ceiling, add `// ponytail:` comment. | **P1** |
| Q22 | `Probing/ComicProbeService.cs:74` extension switch | Sniff first 4 bytes before branching: `50 4B 03 04` → CBZ, `52 61 72 21` → CBR, `37 7A BC AF` → CB7. On extension/magic mismatch, return `(false, "container-extension-mismatch")` with a `renameToExt` hint. Match F-213 semantics (fixer renames, doesn't repack). ~15 lines in a new `SniffAndDispatch` helper. | **P1** |
| Q37 | `Probing/BookProbeService.cs:66` extension switch | Add `".djvu" => ProbeDjvu(path)`. `ProbeDjvu` reads 12 bytes, asserts `AT&TFORM....DJV`. ~15 lines mirroring `ProbePdf`. Removes universal false-positive on every DjVu library file. | **P1** |
| Q7 | `Probing/BookProbeService.cs:165` | Accept `TEXtREAd` alongside `BOOKMOBI` / `TPZ ` in `ProbeMobiFamily`. One-token whitelist widen. | **P1** |

No P0s. The book/comic surface has no destructive/modifying fixer (unlike
the video repair ladder), so the entire class of "silent data loss" P0s
that the encoder audit found in `TrackFixer` / `OutputVerifier` cannot
exist here. Every finding above is either:

- **Silent OK on unreadable** (Q4, Q5, Q19, Q20) — book/comic looks
  healthy in the dashboard but no reader outside the DRM/password holder
  can render it. Diagnostic gap, not destructive.
- **False-positive broken** (Q7, Q22, Q37) — legitimate files flagged as
  corrupt because probe grammar is too tight or extension routing is
  literal. User confusion and unnecessary "approve to repair" prompts
  (though the fixer for books just recycle-bins → user could lose a
  file if they blindly approve).

The six fixes cluster into one patch: three additions to
`BookProbeService` (Adobe DRM in EPUB, `/Encrypt` in PDF, DjVu case,
TEXtREAd whitelist), two additions to `ComicProbeService` (magic-sniff
dispatch, encrypted-archive check), and one PlayabilityScanner branch
for the new `"epub-drm-adobe"` / `"pdf-encrypted"` / `"archive-encrypted"`
reasons routing to the existing DRM-informational Issue shape.
