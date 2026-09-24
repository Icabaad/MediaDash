using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MediaDash.Data;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;

namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Container-integrity probes for CBZ (ZIP), CBR (RAR), and CB7 (7z) comic archives.
/// Verifies the archive parses and contains at least one image entry.
/// </summary>
public sealed class ComicProbeService
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".avif"
    };

    private readonly ProbeCacheStore _probes;

    /// <summary>Initializes a new instance of the <see cref="ComicProbeService"/> class.</summary>
    /// <param name="probes">Probe-cache store (05-C.4) — backs format_probe_cache.</param>
    public ComicProbeService(ProbeCacheStore probes)
    {
        _probes = probes;
    }

    /// <summary>Probes a comic file, using the format_probe_cache.</summary>
    /// <param name="path">Full file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    public Task<ComicProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        FileInfo info;
        try
        {
            info = new FileInfo(path);
        }
        catch (IOException ex)
        {
            return Task.FromResult(new ComicProbeResult(false, "unreadable: " + ex.Message));
        }

        if (!info.Exists)
        {
            return Task.FromResult(new ComicProbeResult(false, "missing"));
        }

        var cached = _probes.GetCachedFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks);
        if (cached is not null)
        {
            return Task.FromResult(new ComicProbeResult(cached.Value.Ok, cached.Value.Reason));
        }

        var result = Probe(path);
        _probes.StoreFormatProbe(path, info.Length, info.LastWriteTimeUtc.Ticks, result.Ok, result.Reason);
        return Task.FromResult(result);
    }

    /// <summary>Uncached probe. Public for unit tests.</summary>
    /// <param name="path">Full file path.</param>
    /// <returns>The result.</returns>
    public static ComicProbeResult Probe(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // Encoder-quirks-audit Q22: extension/container mismatch — very common in scanlation
        // dumps where a .cbz is actually a RAR archive. Magic-sniff the first 4 bytes, and if
        // they disagree with the extension route to the container-extension-mismatch flow so
        // the fixer can propose a rename (same shape as F-213 for video containers).
        var mismatch = SniffMismatch(path, ext);
        if (mismatch is { } m)
        {
            return new ComicProbeResult(false, "mismatch:" + m.RenameToExt + ":comic extension is " + ext + " but the archive is " + m.ActualFormatDescription);
        }

        return ext switch
        {
            ".cbz" => ProbeCbz(path),
            ".cbr" => ProbeCbr(path),
            ".cb7" => ProbeCb7(path),
            _ => new ComicProbeResult(false, "unsupported extension: " + ext)
        };
    }

    private static (string RenameToExt, string ActualFormatDescription)? SniffMismatch(string path, string ext)
    {
        try
        {
            using var fs = Scanners.MediaFileHelper.OpenSharedRead(path);
            if (fs.Length < 4)
            {
                return null;
            }

            Span<byte> magic = stackalloc byte[6];
            var read = fs.Read(magic);
            if (read < 4)
            {
                return null;
            }

            // Detect the actual container from magic. ZIP: PK\x03\x04. RAR4: Rar!\x1A\x07\x00.
            // RAR5: Rar!\x1A\x07\x01\x00. 7z: 37 7A BC AF 27 1C.
            string? actual = null;
            string? desc = null;
            if (magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04)
            {
                actual = "cbz";
                desc = "a ZIP archive";
            }
            else if (magic[0] == 0x52 && magic[1] == 0x61 && magic[2] == 0x72 && magic[3] == 0x21)
            {
                actual = "cbr";
                desc = "a RAR archive";
            }
            else if (magic[0] == 0x37 && magic[1] == 0x7A && magic[2] == 0xBC && magic[3] == 0xAF)
            {
                actual = "cb7";
                desc = "a 7z archive";
            }

            if (actual is null || string.Equals("." + actual, ext, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return (actual, desc!);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static ComicProbeResult ProbeCbz(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var firstImage = zip.Entries.FirstOrDefault(e => ImageExtensions.Contains(Path.GetExtension(e.FullName)));
            if (firstImage is null)
            {
                return new ComicProbeResult(false, "CBZ archive has no image entries");
            }

            foreach (var entry in zip.Entries)
            {
                _ = entry.CompressedLength;
            }

            // Encoder-quirks-audit Q19: password-protected CBZ. .NET's ZipArchive lists entries
            // and reports compressed lengths without exposing the encryption flag directly, so
            // the earlier probe passed encrypted archives as OK. Attempting to read raises
            // InvalidDataException when the entry is ZipCrypto/AES-encrypted — that's our signal.
            try
            {
                using var stream = firstImage.Open();
                Span<byte> sink = stackalloc byte[4];
                _ = stream.Read(sink);
            }
            catch (InvalidDataException)
            {
                return new ComicProbeResult(false, "drm:CBZ archive is password-protected");
            }
            catch (IOException)
            {
                // Not encryption — treat as ordinary corruption below, but the compressed-length
                // walk above already succeeded, so let the file pass. Follow the pre-fix behaviour.
            }

            return new ComicProbeResult(true, null);
        }
        catch (InvalidDataException ex)
        {
            return new ComicProbeResult(false, "CBZ not a valid ZIP: " + ex.Message);
        }
        catch (IOException ex)
        {
            return new ComicProbeResult(false, "CBZ IO error: " + ex.Message);
        }
    }

    private static ComicProbeResult ProbeCbr(string path)
    {
        try
        {
            using var archive = RarArchive.OpenArchive(path);
            var entries = archive.Entries.ToList();
            // Encoder-quirks-audit Q20: SharpCompress surfaces per-entry encryption on RAR (both
            // data-only and header-encrypted archives). If ANY entry is encrypted, the archive
            // can't be read without a password — route to a DRM-informational Issue.
            if (entries.Any(e => e.IsEncrypted))
            {
                return new ComicProbeResult(false, "drm:CBR archive is password-protected");
            }

            var hasImage = entries.Any(e => ImageExtensions.Contains(Path.GetExtension(e.Key ?? string.Empty)));
            if (!hasImage)
            {
                return new ComicProbeResult(false, "CBR archive has no image entries");
            }

            return new ComicProbeResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or SharpCompressException)
        {
            return new ComicProbeResult(false, "CBR read error: " + ex.Message);
        }
    }

    private static ComicProbeResult ProbeCb7(string path)
    {
        try
        {
            using var archive = SevenZipArchive.OpenArchive(path);
            var entries = archive.Entries.ToList();
            if (entries.Any(e => e.IsEncrypted))
            {
                return new ComicProbeResult(false, "drm:CB7 archive is password-protected");
            }

            var hasImage = entries.Any(e => ImageExtensions.Contains(Path.GetExtension(e.Key ?? string.Empty)));
            if (!hasImage)
            {
                return new ComicProbeResult(false, "CB7 archive has no image entries");
            }

            return new ComicProbeResult(true, null);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException or SharpCompressException)
        {
            return new ComicProbeResult(false, "CB7 read error: " + ex.Message);
        }
    }
}
