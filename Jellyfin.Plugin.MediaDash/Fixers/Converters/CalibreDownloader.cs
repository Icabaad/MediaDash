using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Downloads Calibre portable (Windows only in Phase 2 — Linux users install via apt / package manager,
/// macOS via cask / dmg). The archive is written to the caller-supplied destination stream; extraction
/// is left to <see cref="CalibreToolchain"/>. The SHA-256 constant below is the byte hash of the
/// Windows portable installer for the pinned version.
/// </summary>
public sealed class CalibreDownloader : IToolchainDownloader
{
    // ponytail: pin to a specific known-good release (9.15.0). Auto-latest-fetch adds a metadata
    // endpoint that calibre-ebook.com doesn't stably expose; version-bump churn beats a runtime
    // lookup that might silently return a wrong SHA.
    private const string WindowsUrl = "https://download.calibre-ebook.com/9.15.0/calibre-portable-installer-9.15.0.exe";
    private const string WindowsSha256 = "55a8c89bc0739a2dc6d496742ea625fccc6dfdec1413eb805805a28e7227536c";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CalibreDownloader> _logger;

    /// <summary>Initializes a new instance of the <see cref="CalibreDownloader"/> class.</summary>
    /// <param name="httpClientFactory">HTTP client factory (Jellyfin provides this).</param>
    /// <param name="logger">The logger.</param>
    public CalibreDownloader(IHttpClientFactory httpClientFactory, ILogger<CalibreDownloader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanDownload => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(Stream destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!CanDownload)
        {
            _logger.LogInformation("CalibreDownloader: not on Windows — declining.");
            return null;
        }

        using var http = _httpClientFactory.CreateClient(nameof(CalibreDownloader));
        http.Timeout = TimeSpan.FromMinutes(15);

        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(WindowsUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Calibre download HTTP request failed.");
            return null;
        }

        var total = response.Content.Headers.ContentLength ?? -1L;
        using var src = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var sha = SHA256.Create();

        var buffer = new byte[81920];
        long copied = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            sha.TransformBlock(buffer, 0, read, null, 0);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (total > 0)
            {
                progress?.Report((double)copied / total);
            }
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        var actualSha = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
        if (!string.Equals(actualSha, WindowsSha256, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Calibre download SHA-256 mismatch. Expected={Expected} Actual={Actual}", WindowsSha256, actualSha);
            return null;
        }

        return actualSha;
    }
}
