using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.Fixers.Converters;

/// <summary>
/// Resolves <c>ebook-convert</c> — Calibre's CLI. Phase 1 checks the system PATH and a user-configured
/// override path but never downloads anything (the injected <see cref="IToolchainDownloader"/>
/// is <see cref="NoOpToolchainDownloader"/> by default). Phase 2 wires a real downloader.
/// </summary>
public sealed class CalibreToolchain
{
    private readonly IToolchainDownloader _downloader;
    private readonly ILogger<CalibreToolchain> _logger;
    private readonly string _dataDir;
    private string? _resolvedPath;

    /// <summary>Initializes a new instance of the <see cref="CalibreToolchain"/> class.</summary>
    /// <param name="downloader">Toolchain downloader; <see cref="NoOpToolchainDownloader"/> in Phase 1.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pluginDataDirectory">Plugin data folder — where downloaded portables extract to.</param>
    public CalibreToolchain(IToolchainDownloader downloader, ILogger<CalibreToolchain> logger, string pluginDataDirectory)
    {
        _downloader = downloader;
        _logger = logger;
        _dataDir = pluginDataDirectory;
    }

    /// <summary>Gets the path to <c>ebook-convert</c> when resolved; null when unavailable.</summary>
    public string? EbookConvertPath => _resolvedPath;

    /// <summary>Gets a value indicating whether the toolchain has resolved successfully.</summary>
    public bool IsAvailable => _resolvedPath is not null;

    /// <summary>
    /// Resolves the toolchain. Order: user-provided override → system PATH → previously-extracted
    /// portable under the plugin data dir → download (Phase 2+; Phase 1's NoOp downloader always fails).
    /// </summary>
    /// <param name="overridePath">User-configured override folder or null.</param>
    /// <param name="progress">Progress reporter for downloads (0..1). Null OK.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resulting availability after any resolve or download attempt.</returns>
    public async Task<ConverterAvailability> EnsureAsync(
        string? overridePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (_resolvedPath is not null && File.Exists(_resolvedPath))
        {
            return ConverterAvailability.Ready;
        }

        // 1. User override.
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var overrideExe = Path.Combine(overridePath, ExecutableName());
            if (File.Exists(overrideExe))
            {
                _resolvedPath = overrideExe;
                return ConverterAvailability.Ready;
            }
        }

        // 2. System PATH.
        var pathExe = FindOnPath(ExecutableName());
        if (pathExe is not null)
        {
            _resolvedPath = pathExe;
            return ConverterAvailability.Ready;
        }

        // 3. Previously-extracted portable under plugin data.
        var extractedExe = Path.Combine(_dataDir, "tools", "calibre", ExecutableName());
        if (File.Exists(extractedExe))
        {
            _resolvedPath = extractedExe;
            return ConverterAvailability.Ready;
        }

        // 4. Download (Phase 1: NoOpToolchainDownloader always returns null).
        if (!_downloader.CanDownload)
        {
            _logger.LogDebug("Calibre unavailable: no downloader wired (Phase 1 default).");
            return ConverterAvailability.Unavailable;
        }

        var archivePath = Path.Combine(_dataDir, "tools", "calibre.download");
        Directory.CreateDirectory(Path.GetDirectoryName(archivePath)!);
        using (var fs = File.Create(archivePath))
        {
            var checksum = await _downloader.DownloadAsync(fs, progress, cancellationToken).ConfigureAwait(false);
            if (checksum is null)
            {
                _logger.LogWarning("Calibre download failed.");
                return ConverterAvailability.Unavailable;
            }
        }

        // Phase 2 will hook checksum verification + archive extraction here.
        _logger.LogInformation("Calibre download saved to {Path}; extraction not implemented until Phase 2.", archivePath);
        return ConverterAvailability.NeedsDownload;
    }

    private static string ExecutableName()
        => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "ebook-convert.exe" : "ebook-convert";

    private static string? FindOnPath(string exeName)
    {
        var envPath = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(envPath))
        {
            return null;
        }

        foreach (var dir in envPath.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
