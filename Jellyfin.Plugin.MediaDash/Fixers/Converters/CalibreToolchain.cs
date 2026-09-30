using System;
using System.Diagnostics;
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
        var previousExtractRoot = Path.Combine(_dataDir, "tools", "calibre");
        var extractedExe = ResolveEbookConvertUnder(previousExtractRoot);
        if (extractedExe is not null)
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

        // ponytail: name the file after what it actually is (a Windows installer EXE).
        // `.download` triggers the Windows "How do you want to open this file?" association
        // prompt whenever the plugin tries to execute it via ShellExecute/cmd.
        var archivePath = Path.Combine(_dataDir, "tools", "calibre-portable-installer.exe");
        // Clear any leftover file from the pre-fix ".download" naming so we don't leave orphans.
        var legacyDownload = Path.Combine(_dataDir, "tools", "calibre.download");
        if (File.Exists(legacyDownload))
        {
            try
            {
                File.Delete(legacyDownload);
            }
            catch (IOException)
            {
            }
        }

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

        // Phase 2.5: extract the downloaded portable installer.
        // The Calibre portable installer accepts the destination as a positional argument
        // when invoked via cmd.exe /C (a console must be attached; without one it opens a GUI picker).
        // Extracted tree: <extractRoot>/Calibre Portable/Calibre/ebook-convert.exe
        var extractRoot = Path.Combine(_dataDir, "tools", "calibre");
        if (Directory.Exists(extractRoot))
        {
            // Nuke any stale extraction (e.g. from an interrupted prior run).
            try
            {
                Directory.Delete(extractRoot, true);
            }
            catch (IOException)
            {
            }
        }

        Directory.CreateDirectory(extractRoot);

        // ponytail: cmd /C is the simplest way to attach a console so the installer runs headlessly
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // Wrap both paths in quotes; cmd /C "exe" "dest"
        psi.ArgumentList.Add("/C");
        psi.ArgumentList.Add(archivePath);
        psi.ArgumentList.Add(extractRoot);

        using (var proc = Process.Start(psi))
        {
            if (proc is null)
            {
                _logger.LogWarning("Failed to launch Calibre installer for extraction.");
                return ConverterAvailability.Unavailable;
            }

            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Calibre installer exited {Exit}", proc.ExitCode);
                return ConverterAvailability.Unavailable;
            }
        }

        // Layout: <extractRoot>/Calibre Portable/Calibre/ebook-convert.exe
        var resolvedExe = ResolveEbookConvertUnder(extractRoot);
        if (resolvedExe is not null)
        {
            _resolvedPath = resolvedExe;

            // Clean up the 200 MB installer now that extraction is done.
            try
            {
                File.Delete(archivePath);
            }
            catch (IOException)
            {
            }

            _logger.LogInformation("Calibre extracted to {Path}", resolvedExe);
            return ConverterAvailability.Ready;
        }

        _logger.LogWarning("Calibre extraction completed but ebook-convert not found under {Root}", extractRoot);
        return ConverterAvailability.Unavailable;
    }

    private static string? ResolveEbookConvertUnder(string extractRoot)
    {
        var candidates = new[]
        {
            Path.Combine(extractRoot, "Calibre Portable", "Calibre", ExecutableName()),
            Path.Combine(extractRoot, "Calibre", ExecutableName()),
            Path.Combine(extractRoot, ExecutableName())
        };
        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
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
