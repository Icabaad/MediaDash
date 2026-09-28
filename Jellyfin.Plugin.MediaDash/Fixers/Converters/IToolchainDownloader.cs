using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Downloads a platform-appropriate Calibre portable archive to a destination stream.
/// Swapped for a file:// stub in tests. Phase 1 ships no production implementation — the
/// only wired instance is <see cref="NoOpToolchainDownloader"/> which reports unavailability.
/// Phase 2 will add a real HTTPS downloader against Calibre's CDN with a baked-in checksum.
/// </summary>
public interface IToolchainDownloader
{
    /// <summary>Gets a value indicating whether a downloader is actually configured. Phase 1 default returns false.</summary>
    bool CanDownload { get; }

    /// <summary>Writes the archive bytes into <paramref name="destination"/>. Returns null on failure.</summary>
    /// <param name="destination">Destination stream for the archive.</param>
    /// <param name="progress">Optional progress reporter, 0..1.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>SHA-256 checksum of the downloaded bytes, or null on failure.</returns>
    Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken);
}
