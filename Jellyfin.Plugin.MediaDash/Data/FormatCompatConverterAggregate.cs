using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Per-converter analytics for FormatCompatibility fix history within a single month.
/// </summary>
/// <param name="FixedByConverter">Number of successful, non-dry-run fixes keyed by converter string.</param>
/// <param name="BytesReclaimedByConverter">
/// Net bytes reclaimed keyed by converter string. Positive = source larger than target (common);
/// negative = target larger than source (e.g. EPUB generated from plain-text MOBI).
/// </param>
public sealed record FormatCompatConverterAggregate(
    IReadOnlyDictionary<string, int> FixedByConverter,
    IReadOnlyDictionary<string, long> BytesReclaimedByConverter);
