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
    /// <summary>Gets the registry key referenced by <see cref="FormatEntry.ConverterKey"/>.</summary>
    string Key { get; }

    /// <summary>Gets the media kind this converter handles.</summary>
    MediaKind Kind { get; }

    /// <summary>
    /// Gets the collection types under which this converter is allowed to touch files. Enforced at both
    /// scan-emit time and fix-time. See spec §5.
    /// </summary>
    IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; }

    /// <summary>Gets a value indicating whether the converter's toolchain is present. Cheap — no I/O beyond file-exists.</summary>
    ConverterAvailability Availability { get; }

    /// <summary>
    /// Gets a value indicating whether the source extension is one this converter knows how to handle.
    /// </summary>
    /// <param name="sourceExtension">Lowercased extension including the dot (e.g. ".mobi").</param>
    /// <returns>True when this converter can produce output for the given extension.</returns>
    bool CanConvert(string sourceExtension);

    /// <summary>
    /// Ensures the toolchain is available. May trigger a lazy download the first time.
    /// Phase 1 implementations are permitted to return <see cref="ConverterAvailability.Unavailable"/>
    /// unconditionally; real conversion lands in Phase 2+.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for a long-running download.</param>
    /// <returns>The resulting availability after any download work completes.</returns>
    Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken);

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
}
