using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Container-integrity probes for EPUB, PDF, MOBI, and AZW/AZW3.
/// Verifies structural correctness only — never decrypts or converts.
/// </summary>
public sealed class BookProbeService
{
    private readonly ProbeCacheStore _probes;

    /// <summary>Initializes a new instance of the <see cref="BookProbeService"/> class.</summary>
    /// <param name="probes">Probe-cache store (05-C.4) — backs format_probe_cache.</param>
    public BookProbeService(ProbeCacheStore probes)
    {
        _probes = probes;
    }

    /// <summary>Probes a book file, using the cache.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public Task<BookProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException ex)
        {
            return Task.FromResult(new BookProbeResult(false, "unreadable: " + ex.Message));
        }

        if (!info.Exists)
        {
            return Task.FromResult(new BookProbeResult(false, "missing"));
        }

        var cached = _probes.GetCachedFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (cached is not null)
        {
            return Task.FromResult(new BookProbeResult(cached.Value.Ok, cached.Value.Reason));
        }

        var result = Probe(path);
        _probes.StoreFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks, result.Ok, result.Reason);
        return Task.FromResult(result);
    }

    /// <summary>Uncached probe dispatched on file extension. Public for unit tests.</summary>
    /// <param name="path">Full file path.</param>
    /// <returns>The result.</returns>
    public static BookProbeResult Probe(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".epub" => ProbeEpub(path),
            ".pdf" => ProbePdf(path),
            ".mobi" or ".azw" or ".azw3" => ProbeMobiFamily(path),
            ".djvu" => ProbeDjvu(path),
            _ => new BookProbeResult(false, "unsupported extension: " + ext)
        };
    }

    private static BookProbeResult ProbeEpub(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var mimetype = zip.GetEntry("mimetype");
            if (mimetype is null)
            {
                return new BookProbeResult(false, "EPUB missing mimetype entry");
            }

            using var stream = mimetype.Open();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            var contents = reader.ReadToEnd().Trim();
            if (contents != "application/epub+zip")
            {
                return new BookProbeResult(false, "EPUB mimetype content wrong: '" + contents + "'");
            }

            // Adobe DRM (encoder-quirks-audit Q4): a META-INF/encryption.xml with a CipherReference
            // pointing at a non-font resource is Adobe Digital Editions encryption. Calibre uses the
            // same file for font obfuscation (all URIs point to fonts under OEBPS/fonts/*), which is
            // NOT DRM — the book itself is readable. Only surface as DRM when non-font references
            // exist. Emit the "drm:" prefix so PlayabilityScanner routes to a DRM-informational
            // Issue instead of the corruption/repair path.
            var encryptionEntry = zip.GetEntry("META-INF/encryption.xml");
            if (encryptionEntry is not null)
            {
                try
                {
                    using var encStream = encryptionEntry.Open();
                    using var encReader = new StreamReader(encStream, Encoding.UTF8);
                    var encXml = encReader.ReadToEnd();
                    if (ContainsNonFontCipherReference(encXml))
                    {
                        return new BookProbeResult(false, "drm:EPUB is encrypted with Adobe Digital Editions DRM");
                    }
                }
                catch (IOException)
                {
                    // If we can't read encryption.xml, fall through — the entry-walk below will still
                    // check compressed lengths, which surfaces genuine corruption.
                }
            }

            foreach (var entry in zip.Entries)
            {
                _ = entry.CompressedLength;
            }

            return new BookProbeResult(true, null);
        }
        catch (InvalidDataException ex)
        {
            return new BookProbeResult(false, "EPUB not a valid ZIP: " + ex.Message);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "EPUB IO error: " + ex.Message);
        }
    }

    // Adobe DRM's encryption.xml lists CipherReference URIs of every encrypted resource. Calibre's
    // font-obfuscation uses the same structure but every URI points at OEBPS/fonts/*.ttf/otf/woff.
    // Return true if ANY reference is not a font — that's the Adobe DRM signal.
    private static bool ContainsNonFontCipherReference(string encryptionXml)
    {
        var offset = 0;
        while (true)
        {
            var refIdx = encryptionXml.IndexOf("CipherReference", offset, StringComparison.Ordinal);
            if (refIdx < 0)
            {
                return false;
            }

            var uriIdx = encryptionXml.IndexOf("URI=", refIdx, StringComparison.Ordinal);
            if (uriIdx < 0)
            {
                return false;
            }

            var quote = encryptionXml[uriIdx + 4];
            var uriStart = uriIdx + 5;
            var uriEnd = encryptionXml.IndexOf(quote, uriStart);
            if (uriEnd < 0)
            {
                return false;
            }

            var uri = encryptionXml.Substring(uriStart, uriEnd - uriStart).ToLowerInvariant();
            var isFont = uri.EndsWith(".ttf", StringComparison.Ordinal)
                || uri.EndsWith(".otf", StringComparison.Ordinal)
                || uri.EndsWith(".woff", StringComparison.Ordinal)
                || uri.EndsWith(".woff2", StringComparison.Ordinal);
            if (!isFont)
            {
                return true;
            }

            offset = uriEnd + 1;
        }
    }

    private static BookProbeResult ProbePdf(string path)
    {
        try
        {
            using var fs = Scanners.MediaFileHelper.OpenSharedRead(path);
            if (fs.Length < 8)
            {
                return new BookProbeResult(false, "PDF too small");
            }

            Span<byte> header = stackalloc byte[8];
            fs.ReadExactly(header);
            if (!(header[0] == 0x25 && header[1] == 0x50 && header[2] == 0x44 && header[3] == 0x46 && header[4] == 0x2D))
            {
                return new BookProbeResult(false, "PDF missing %PDF- header");
            }

            var tailLength = (int)Math.Min(1024, fs.Length);
            fs.Seek(-tailLength, SeekOrigin.End);
            Span<byte> tail = stackalloc byte[tailLength];
            fs.ReadExactly(tail);
            var tailText = Encoding.ASCII.GetString(tail);
            if (!tailText.Contains("%%EOF", StringComparison.Ordinal))
            {
                return new BookProbeResult(false, "PDF missing %%EOF trailer");
            }

            if (!tailText.Contains("startxref", StringComparison.Ordinal))
            {
                return new BookProbeResult(false, "PDF missing startxref");
            }

            // Encrypted PDF (encoder-quirks-audit Q5): the trailer dict contains an /Encrypt token
            // when the PDF is password-protected. ponytail: linearised PDFs may push the trailer
            // further in than our 1 KiB tail, so this is a lower-bound detector — some encrypted
            // PDFs will slip past and fall through to the corruption path. Enough to catch the
            // common case.
            if (tailText.Contains("/Encrypt", StringComparison.Ordinal))
            {
                return new BookProbeResult(false, "drm:PDF is password-protected");
            }

            return new BookProbeResult(true, null);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "PDF IO error: " + ex.Message);
        }
    }

    private static BookProbeResult ProbeMobiFamily(string path)
    {
        try
        {
            using var fs = Scanners.MediaFileHelper.OpenSharedRead(path);
            if (fs.Length < 78)
            {
                return new BookProbeResult(false, "MOBI too small for PalmDoc header");
            }

            fs.Seek(60, SeekOrigin.Begin);
            Span<byte> typeBytes = stackalloc byte[8];
            fs.ReadExactly(typeBytes);
            var type = Encoding.ASCII.GetString(typeBytes);
            // Encoder-quirks-audit Q7: pre-KF8 .azw files carry `TEXtREAd` here instead of
            // `BOOKMOBI` — the original PalmDoc reader ID. Widened to accept.
            if (type != "BOOKMOBI" && type != "TPZ " && type != "TEXtREAd")
            {
                return new BookProbeResult(false, "MOBI type magic not recognised: '" + type + "'");
            }

            return new BookProbeResult(true, null);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "MOBI IO error: " + ex.Message);
        }
    }

    // Encoder-quirks-audit Q37: .djvu was listed in MediaFormats.Books but there was no probe
    // branch, so every DjVu file in a Book library got flagged as "unsupported extension" and
    // routed to book-or-comic-corrupt. Add the missing case with the DjVu chunk-header magic.
    private static BookProbeResult ProbeDjvu(string path)
    {
        try
        {
            using var fs = Scanners.MediaFileHelper.OpenSharedRead(path);
            if (fs.Length < 16)
            {
                return new BookProbeResult(false, "DjVu too small for chunk header");
            }

            Span<byte> header = stackalloc byte[16];
            fs.ReadExactly(header);
            // DjVu files start with AT&TFORM (8 bytes) followed by a big-endian length (4 bytes)
            // and a 4-byte format id: DJVU (single-page), DJVM (multi-page container).
            if (!(header[0] == 0x41 && header[1] == 0x54 && header[2] == 0x26 && header[3] == 0x54
                && header[4] == 0x46 && header[5] == 0x4F && header[6] == 0x52 && header[7] == 0x4D))
            {
                return new BookProbeResult(false, "DjVu missing AT&TFORM header");
            }

            var formatId = Encoding.ASCII.GetString(header[12..16]);
            if (formatId != "DJVU" && formatId != "DJVM")
            {
                return new BookProbeResult(false, "DjVu format id not recognised: '" + formatId + "'");
            }

            return new BookProbeResult(true, null);
        }
        catch (IOException ex)
        {
            return new BookProbeResult(false, "DjVu IO error: " + ex.Message);
        }
    }
}
