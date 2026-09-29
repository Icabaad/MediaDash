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

## Task-9 stub note

Several formats cannot be sourced as < 200 KB real files without ffmpeg (not available in the
sourcing environment) or because the format's minimum parseable representation is inherently
larger than the magic-header stub. Stubs contain only the format's magic bytes / container
header so the scanner's format-detection branch is exercised. They are NOT valid decodable
files; any test that depends on decoding content (not just detecting the format) must generate
a real file at test-time or be marked `[Trait("Category","LargeFixture")]`.

## Provenance

| File | Source | License |
|------|--------|---------|
| `ebook/sample.mobi` | Project Gutenberg cache — Alice in Wonderland (pg11.mobi, no-images build) https://www.gutenberg.org/cache/epub/11/pg11.mobi | Public domain (pre-1928 text) |
| `ebook/sample.azw` | Header-only stub — PalmDB container with BOOK/MOBI type+creator codes; see task-9 note | Self-generated |
| `ebook/sample.azw3` | Header-only stub — PalmDB container with BOOK/MOBI type+creator codes (KF8 uses same PalmDB wrapper); see task-9 note | Self-generated |
| `ebook/sample.djvu` | Header-only stub — AT&TFORM+DJVU magic (16 bytes); see task-9 note | Self-generated |
| `ebook/sample.txt` | Project Gutenberg — Alice's Adventures in Wonderland plain text https://www.gutenberg.org/ebooks/11.txt.utf-8 | Public domain (pre-1928 text) |
| `ebook/sample.rtf` | Self-generated via PowerShell — minimal well-formed RTF 1.0 document (no copyrighted content) | Self-generated (public domain) |
| `ebook/sample.doc` | Header-only stub — OLE2 Compound Document magic (D0 CF 11 E0 A1 B1 1A E1); see task-9 note | Self-generated |
| `ebook/sample.docx` | Self-generated via PowerShell — minimal OOXML ZIP with Content_Types.xml + word/document.xml | Self-generated (public domain) |
| `ebook/sample.fb2` | Self-generated via PowerShell — minimal FictionBook 2.0 XML document (no copyrighted content) | Self-generated (public domain) |
| `ebook/sample.lit` | Header-only stub — ITOLITLS magic (8 bytes); see task-9 note | Self-generated |
| `ebook/sample.lrf` | Header-only stub — Sony BBeB LRF magic (L\0 + BBeB at offset 8); see task-9 note | Self-generated |
| `ebook/sample.pdb` | Header-only stub — PalmDB with TEXt/REAd type+creator for Palm eReader; see task-9 note | Self-generated |
| `ebook/sample.htmlz` | Self-generated via PowerShell — minimal ZIP containing index.html + metadata.opf | Self-generated (public domain) |
| `ebook/sample.chm` | Header-only stub — ITSF v3 magic (4 bytes + version); see task-9 note | Self-generated |
| `comic/sample.cbr` | Real RAR5 archive — 3 procedurally-generated 32×32 CC0 JPGs (solid red, green, blue) packed with WinRAR 7.11 (`Rar.exe a -ep`); replaces the task-9 header stub | Self-generated (public domain) |
| `comic/sample.cb7` | Header-only stub — 7z magic (37 7A BC AF 27 1C); see task-9 note | Self-generated |
| `audiobook/sample.aax` | Header-only stub — 32 bytes starting with AA\x00\x00; test asserts DRM advisory only, not audio validity | Self-generated |
| `image/sample.heic` | Header-only stub — ISO BMFF ftyp box with heic major brand; see task-9 note | Self-generated |
| `image/sample.heif` | Header-only stub — ISO BMFF ftyp box with heif major brand; see task-9 note | Self-generated |
| `image/sample.tif` | Self-generated via System.Drawing — 32x32 colour test-pattern, TIFF encoding | Self-generated (public domain) |
| `image/sample.tiff` | Copy of sample.tif (alternate extension alias) | Self-generated (public domain) |
| `image/sample.bmp` | Self-generated via System.Drawing — 32x32 colour test-pattern, BMP encoding | Self-generated (public domain) |
| `image/sample.raw` | Header-only stub — TIFF little-endian magic (II*\0); RAW formats are TIFF-derived; see task-9 note | Self-generated |
| `image/sample.nef` | Header-only stub — TIFF LE magic (Nikon NEF is TIFF-derived); see task-9 note | Self-generated |
| `image/sample.cr2` | Header-only stub — TIFF LE magic + CR\x02\x00 CR2 identifier at offset 8; see task-9 note | Self-generated |
| `image/sample.arw` | Header-only stub — TIFF big-endian magic (MM\0*); Sony ARW is TIFF-BE-derived; see task-9 note | Self-generated |
| `image/sample.dng` | Header-only stub — TIFF LE magic (Adobe DNG is TIFF-derived); see task-9 note | Self-generated |
| `music/sample.669` | Header-only stub — UNIS 669 "if" magic (2 bytes); see task-9 note | Self-generated |
| `music/sample.it` | Header-only stub — Impulse Tracker "IMPM" magic (4 bytes); see task-9 note | Self-generated |
| `music/sample.mod` | Header-only stub — Protracker MOD with "M.K." marker at offset 1080; see task-9 note | Self-generated |
| `music/sample.s3m` | Header-only stub — ScreamTracker 3 "SCRM" magic at offset 44; see task-9 note | Self-generated |
| `music/sample.xm` | Header-only stub — FastTracker 2 "Extended Module: " header string + 0x1A byte; see task-9 note | Self-generated |
| `music/sample.ape` | Header-only stub — Monkey's Audio "MAC " magic (4 bytes); see task-9 note | Self-generated |
| `music/sample.wv` | Header-only stub — WavPack "wvpk" magic (4 bytes); see task-9 note | Self-generated |
| `music/sample.tak` | Header-only stub — TAK magic bytes (E2 8F A7 2F); see task-9 note | Self-generated |
| `music/sample.tta` | Header-only stub — True Audio "TTA1" magic (4 bytes); see task-9 note | Self-generated |
| `music/sample.wma` | Header-only stub — ASF container GUID (30 26 B2 75 8E 66 CF 11 A6 D9 00 AA 00 62 CE 6C); see task-9 note | Self-generated |
| `video/sample.rmvb` | Header-only stub — RealMedia ".RMF" magic (2E 52 4D 46) + size + version; see task-9 note | Self-generated |
