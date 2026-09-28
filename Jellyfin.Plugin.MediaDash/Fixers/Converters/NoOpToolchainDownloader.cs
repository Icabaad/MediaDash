using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>Default Phase 1 downloader — always reports unavailability, never downloads.</summary>
public sealed class NoOpToolchainDownloader : IToolchainDownloader
{
    /// <inheritdoc />
    public bool CanDownload => false;

    /// <inheritdoc />
    public Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
}
