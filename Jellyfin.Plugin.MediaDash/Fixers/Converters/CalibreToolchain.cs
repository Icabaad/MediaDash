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
    // External audit P2 #8: tests used to resolve the host-installed Calibre via FindOnPath
    // (environment PATH), which turned "no executable available" tests into false passes on
    // developer machines with Calibre installed. Internal seam so tests inject a resolver that
    // returns null; production ctor keeps the FindOnPath behaviour.
    private readonly Func<string, string?> _pathResolver;
    private string? _resolvedPath;

    // External review #9: track the override folder that produced the cached resolution so a
    // mid-session override change invalidates the cache. Without this a user who repointed
    // CalibreOverridePath to a different installation kept getting the old executable until
    // the server restarted.
    private string? _cachedOverride;

    /// <summary>Initializes a new instance of the <see cref="CalibreToolchain"/> class.</summary>
    /// <param name="downloader">Toolchain downloader; <see cref="NoOpToolchainDownloader"/> in Phase 1.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="pluginDataDirectory">Plugin data folder — where downloaded portables extract to.</param>
    public CalibreToolchain(IToolchainDownloader downloader, ILogger<CalibreToolchain> logger, string pluginDataDirectory)
        : this(downloader, logger, pluginDataDirectory, pathResolver: FindOnPath)
    {
    }

    // ponytail: internal seam so tests can bypass the host PATH scan — pass a resolver that
    // returns null to simulate a machine with no Calibre installed, or a lambda that returns a
    // specific temp exe path to simulate a specific PATH entry. See external audit P2 #8.
    internal CalibreToolchain(
        IToolchainDownloader downloader,
        ILogger<CalibreToolchain> logger,
        string pluginDataDirectory,
        Func<string, string?> pathResolver)
    {
        _downloader = downloader;
        _logger = logger;
        _dataDir = pluginDataDirectory;
        _pathResolver = pathResolver;
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
        // External review #9: if the user changed the override folder since the last successful
        // resolve, invalidate the cache. Case-insensitive compare — Windows paths; harmless on
        // Linux because any genuine change produces a different string either way.
        var normalisedOverride = string.IsNullOrWhiteSpace(overridePath) ? null : overridePath;
        if (_resolvedPath is not null && !string.Equals(_cachedOverride, normalisedOverride, StringComparison.OrdinalIgnoreCase))
        {
            _resolvedPath = null;
        }

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
                _cachedOverride = normalisedOverride;
                return ConverterAvailability.Ready;
            }
        }

        // 2. System PATH (injected resolver for tests; production uses FindOnPath).
        var pathExe = _pathResolver(ExecutableName());
        if (pathExe is not null)
        {
            _resolvedPath = pathExe;
            _cachedOverride = normalisedOverride;
            return ConverterAvailability.Ready;
        }

        // 3. Previously-extracted portable under plugin data.
        var previousExtractRoot = Path.Combine(_dataDir, "tools", "calibre");
        var extractedExe = ResolveEbookConvertUnder(previousExtractRoot);
        if (extractedExe is not null)
        {
            _resolvedPath = extractedExe;
            _cachedOverride = normalisedOverride;
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

        // External review #8: cmd.exe's /C flag has a quirky parse rule when the command line has
        // multiple quoted arguments — it strips the outermost pair unless the WHOLE command is
        // additionally wrapped in quotes. ProcessStartInfo.ArgumentList auto-quotes each arg
        // individually, which produces `/C "C:\Program Files\installer.exe" "C:\dest"`; cmd then
        // interprets that as "run `C:\Program` with the rest as args" and the installer never
        // launches. The documented workaround is to wrap the entire command in an extra outer
        // pair: `/C ""C:\Program Files\installer.exe" "C:\dest""`. ArgumentList can't produce
        // that shape — we have to set Arguments directly and escape internal quotes manually.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = "/C \"\"" + archivePath + "\" \"" + extractRoot + "\"\""
        };

        // External audit P2 #7: drain stdout/stderr, bound extraction time, and kill the
        // process tree on cancellation or timeout. Pre-audit this method set RedirectStandardOutput
        // + RedirectStandardError but never read either, so a sufficiently verbose installer
        // could fill its 4 KB pipe buffer and deadlock. WaitForExitAsync with just the user's CT
        // let a hung child outlive the cancellation (Dispose doesn't kill child processes), so
        // the next EnsureAsync could race against the still-running installer.
        var proc = Process.Start(psi);
        if (proc is null)
        {
            _logger.LogWarning("Failed to launch Calibre installer for extraction.");
            return ConverterAvailability.Unavailable;
        }

        try
        {
            // Drain both streams so the child never blocks writing to a full pipe.
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            // 15 minutes covers the slowest observed portable extract (200 MB installer onto a
            // spinning disk) with headroom; anything beyond that is a stuck installer and must be
            // killed so the user isn't blocked indefinitely.
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(15));
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                KillTree(proc);
                _logger.LogWarning("Calibre installer timed out after 15 minutes; process tree killed.");
                return ConverterAvailability.Unavailable;
            }
            catch (OperationCanceledException)
            {
                KillTree(proc);
                throw;
            }

            if (proc.ExitCode != 0)
            {
                _logger.LogWarning("Calibre installer exited {Exit}", proc.ExitCode);
                return ConverterAvailability.Unavailable;
            }
        }
        finally
        {
            proc.Dispose();
        }

        // Layout: <extractRoot>/Calibre Portable/Calibre/ebook-convert.exe
        var resolvedExe = ResolveEbookConvertUnder(extractRoot);
        if (resolvedExe is not null)
        {
            _resolvedPath = resolvedExe;
            _cachedOverride = normalisedOverride;

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

    // Kill the whole process tree so the installer's spawned grandchildren don't outlive the
    // top-level cmd.exe. .NET 5+ Process.Kill(entireProcessTree: true) is the documented way to
    // make this reliable across Windows job objects and POSIX process groups.
    private static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the HasExited check and the kill.
        }
        catch (NotSupportedException)
        {
            // Running against a Process we don't own (shouldn't happen for Process.Start'd procs).
        }
    }

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
