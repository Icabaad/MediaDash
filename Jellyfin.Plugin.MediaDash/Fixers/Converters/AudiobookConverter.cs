using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Scanners;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Handles audiobook format issues. Phase 3-1: DRM refusal for AAX/AA.
/// Phase 3-3+ will extend this with folder-merge for multi-file audiobooks.
/// </summary>
public sealed class AudiobookConverter : IFormatConverter
{
    private static readonly HashSet<string> Handled = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aax", ".aa"
        // ponytail: folder-merge canconvert lands in Phase 3-3
    };

    /// <inheritdoc />
    public string Key => "audiobook";

    /// <inheritdoc />
    public MediaKind Kind => MediaKind.Audiobook;

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
    public Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var ext = Path.GetExtension(sourcePath);
        if (Handled.Contains(ext))
        {
            return Task.FromResult(new ConversionResult(
                false,
                null,
                "This file is DRM-protected (Audible AAX/AA). MediaDash cannot legally convert DRM. Use Audible's own app or a licensed conversion tool, then re-import as M4B."));
        }

        // ponytail: folder-merge not implemented yet — Phase 3-3
        return Task.FromResult(new ConversionResult(false, null, "Folder merge not implemented yet."));
    }
}
