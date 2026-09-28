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
