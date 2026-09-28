using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
    public ConverterAvailability Availability => ConverterAvailability.Ready;

    /// <inheritdoc />
    public bool CanConvert(string sourceExtension) => Handled.Contains(sourceExtension);

    /// <inheritdoc />
    public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
        => Task.FromResult(ConverterAvailability.Ready);

    /// <inheritdoc />
    public async Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        try
        {
            var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
            using var source = OpenArchive(sourcePath, ext);
            var entries = source.Entries.Where(e => !e.IsDirectory).ToList();
            var total = entries.Count;

            Directory.CreateDirectory(Path.GetDirectoryName(stagedTargetPath)!);
            using var fs = File.Create(stagedTargetPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

            for (var i = 0; i < entries.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = entries[i];
                var name = entry.Key ?? Guid.NewGuid().ToString();
                var zipEntry = zip.CreateEntry(name, CompressionLevel.NoCompression);
                var src = await entry.OpenEntryStreamAsync(cancellationToken).ConfigureAwait(false);
                await using (src.ConfigureAwait(false))
                {
                    var dst = zipEntry.Open();
                    await using (dst.ConfigureAwait(false))
                    {
                        await src.CopyToAsync(dst, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (total > 0)
                {
                    progress?.Report(1.0 * (i + 1) / total);
                }
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
            _logger.LogWarning(ex, "Comic conversion failed: {Source} -> {Target}", sourcePath, stagedTargetPath);
            SafeDelete(stagedTargetPath);
            return new ConversionResult(false, null, "Comic conversion failed: " + ex.Message, ex.ToString());
        }
    }

    private static IArchive OpenArchive(string sourcePath, string ext)
        => ext switch
        {
            ".cbr" => RarArchive.OpenArchive(sourcePath),
            ".cb7" => SevenZipArchive.OpenArchive(sourcePath),
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
