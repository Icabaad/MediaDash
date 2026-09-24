using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Scheduled task that runs all enabled scanners across the media libraries.
/// </summary>
public sealed class ScanTask : IScheduledTask
{
    /// <summary>
    /// Grace window a user-triggered scan grants the reviewer before opportunistic fixes are
    /// allowed to run. Long enough to actually read the Issues tab and decide, short enough that
    /// forgotten-tab scenarios still get their auto-fix on the next scheduled fix window.
    /// </summary>
    internal static readonly TimeSpan ManualScanReviewGrace = TimeSpan.FromMinutes(10);

    private readonly ILibraryManager _libraryManager;
    private readonly IEnumerable<IScanner> _scanners;
    private readonly MediaDashDb _db;
    private readonly ISessionManager _sessionManager;
    private readonly RunState _runState;
    private readonly IPluginConfigurationProvider _configProvider;
    private readonly ILogger<ScanTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScanTask"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="scanners">All registered scanners.</param>
    /// <param name="db">The plugin database.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="runState">The shared run-state holder (02-A).</param>
    /// <param name="configProvider">Configuration snapshot provider (02-A).</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ScanTask}"/> interface.</param>
    public ScanTask(
        ILibraryManager libraryManager,
        IEnumerable<IScanner> scanners,
        MediaDashDb db,
        ISessionManager sessionManager,
        RunState runState,
        IPluginConfigurationProvider configProvider,
        ILogger<ScanTask> logger)
    {
        _libraryManager = libraryManager;
        _scanners = scanners;
        _db = db;
        _sessionManager = sessionManager;
        _runState = runState;
        _configProvider = configProvider;
        _logger = logger;
        // 02-A: bridge the DI singleton to Plugin.AttachedRunState so the legacy Plugin.CurrentActivity /
        // ScanScannersDone/Total facades (still used by ProbingScannerBase and the older tests) reach
        // this same instance. Idempotent — FixTask's ctor also does this on first resolution.
        Plugin.AttachedRunState = runState;
    }

    /// <inheritdoc />
    public string Name => I18n.I18nCatalog.GetHtml(System.Globalization.CultureInfo.CurrentUICulture.Name, "task.scan.name", "Scan libraries for issues");

    /// <inheritdoc />
    public string Key => "MediaDashScan";

    /// <inheritdoc />
    public string Description => I18n.I18nCatalog.GetHtml(System.Globalization.CultureInfo.CurrentUICulture.Name, "task.scan.description", "Looks for duplicates, unplayable files, oversized encodes, unwanted language tracks, misplaced files and videos missing subtitles.");

    /// <inheritdoc />
    public string Category => "MediaDash";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var bypassIdleCheck = _runState.ConsumeBypassIdleCheck(RunRole.Scan);
        // 02-A: capture a coherent snapshot once at run start so a config save mid-scan can't
        // flip the idle policy or Off-type filter halfway through. Later per-item checks that
        // legitimately want to observe drift (ScanTask has none today; FixTask has them at
        // line ~575) re-call the provider.
        var runConfig = _configProvider.GetSnapshot();
        if (runConfig.PauseDuringPlayback && !bypassIdleCheck && IdleCheck.IsServerBusy(_sessionManager))
        {
            _logger.LogInformation("Skipping scheduled scan: someone is watching or was recently active.");
            progress.Report(100);
            return;
        }

        if (!_runState.TryBegin(RunRole.Scan))
        {
            _logger.LogInformation("Skipping scheduled scan: another scan is already running in this process.");
            progress.Report(100);
            return;
        }

        try
        {
            await ExecuteScanBodyAsync(bypassIdleCheck, runConfig, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _runState.Complete(RunRole.Scan);
        }
    }

    private async Task ExecuteScanBodyAsync(bool bypassIdleCheck, PluginConfiguration runConfig, IProgress<double> progress, CancellationToken cancellationToken)
    {
        // ponytail: widened for v0.9 to include non-video kinds (music, audiobook, book, comic)
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes =
            [
                BaseItemKind.Movie,
                BaseItemKind.Episode,
                BaseItemKind.Audio,
                BaseItemKind.AudioBook,
                BaseItemKind.Book,
                BaseItemKind.MusicVideo
            ],
            IsVirtualItem = false,
            Recursive = true
        });

        // Jellyfin indexes sidecar theme.mp3 / themevideo.* as ordinary Audio/Video items alongside
        // the parent series/movie. They should not be checked as library content — no subs, no
        // duplicates, no "unplayable" — so drop them before the scanners see them.
        items = items.Where(i => !i.IsThemeMedia).ToList();

        // One-time content-aware defaults sweep: for each scanner whose content type isn't present
        // in the user's libraries AND whose FixMode is still the untouched default (DetectOnly),
        // silence the scanner (Off). Stops empty scanner cycles and de-clutters the Settings UI.
        // Gated by a plugin_state key so it only ever runs once per install/upgrade — a user who
        // adds new content types later can re-enable the scanners in Settings.
        if (_db.GetState("ContentAwareDefaultsChecked") is null)
        {
            var hasVideo = items.Any(i => i.GetBaseItemKind() is BaseItemKind.Movie or BaseItemKind.Episode or BaseItemKind.MusicVideo);
            var hasAudio = items.Any(i => i.GetBaseItemKind() is BaseItemKind.Audio or BaseItemKind.AudioBook);
            var scanConfig = Plugin.Instance!.Configuration;
            var silenced = new List<string>();

            // Audio-only defaults.
            if (!hasAudio && scanConfig.EmbeddedCoverFixMode == Configuration.FixMode.DetectOnly)
            {
                scanConfig.EmbeddedCoverFixMode = Configuration.FixMode.Off;
                silenced.Add("EmbeddedCoverArt");
            }

            // Video-only defaults — every scanner in this block operates on video streams,
            // subtitle sidecars for video, video-transcode logs, or trickplay sprites Jellyfin
            // only generates for video items. Books and audio have their own pipelines.
            if (!hasVideo)
            {
                void Silence(Func<Configuration.FixMode> get, Action<Configuration.FixMode> set, string label)
                {
                    if (get() == Configuration.FixMode.DetectOnly)
                    {
                        set(Configuration.FixMode.Off);
                        silenced.Add(label);
                    }
                }

                Silence(() => scanConfig.TranscodeFixMode, v => scanConfig.TranscodeFixMode = v, "Quality");
                Silence(() => scanConfig.SubtitleFixMode, v => scanConfig.SubtitleFixMode = v, "SubtitleLanguage");
                Silence(() => scanConfig.AudioFixMode, v => scanConfig.AudioFixMode = v, "AudioLanguage");
                Silence(() => scanConfig.MissingSubtitlesFixMode, v => scanConfig.MissingSubtitlesFixMode = v, "MissingSubtitles");
                Silence(() => scanConfig.SubtitleFontFixMode, v => scanConfig.SubtitleFontFixMode = v, "SubtitleFonts");
                Silence(() => scanConfig.TrickplayFixMode, v => scanConfig.TrickplayFixMode = v, "LargeTrickplay");
                Silence(() => scanConfig.HeavyTranscodeFixMode, v => scanConfig.HeavyTranscodeFixMode = v, "HeavyTranscode");
                Silence(() => scanConfig.FailedTranscodeFixMode, v => scanConfig.FailedTranscodeFixMode = v, "FailedTranscode");
                Silence(() => scanConfig.UngroupedFixMode, v => scanConfig.UngroupedFixMode = v, "Ungrouped");
            }

            if (silenced.Count > 0)
            {
                Plugin.Instance!.SaveConfiguration();
                _logger.LogInformation(
                    "Content-aware defaults sweep: silenced {Count} scanner(s) whose content type isn't present in this library (hasVideo={HasVideo}, hasAudio={HasAudio}): {Silenced}. Re-enable in Settings if you add the relevant content later.",
                    silenced.Count,
                    hasVideo,
                    hasAudio,
                    string.Join(", ", silenced));
            }

            _db.SetState("ContentAwareDefaultsChecked", DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        }

        // Overlapping-library warning: users with a "Media" library covering /media plus separate
        // "Movies" (/media/movies) and "TV" (/media/tv) libraries produce duplicate metadata,
        // double-counted stats, and phantom Ungrouped / Misplaced findings that Jellyfin's own scans
        // trigger against overlapping trees. Emit a single dedup'd diagnostic per pair so the user
        // sees the collision. Cheap: N is small (~a dozen libraries at the extreme).
        var virtualFolders = _libraryManager.GetVirtualFolders();
        for (var i = 0; i < virtualFolders.Count; i++)
        {
            var outer = virtualFolders[i];
            foreach (var outerPath in outer.Locations ?? Array.Empty<string>())
            {
                var outerNormalised = System.IO.Path.TrimEndingDirectorySeparator(outerPath) + System.IO.Path.DirectorySeparatorChar;
                for (var j = 0; j < virtualFolders.Count; j++)
                {
                    if (j == i)
                    {
                        continue;
                    }

                    var inner = virtualFolders[j];
                    foreach (var innerPath in inner.Locations ?? Array.Empty<string>())
                    {
                        var innerNormalised = System.IO.Path.TrimEndingDirectorySeparator(innerPath) + System.IO.Path.DirectorySeparatorChar;
                        if (innerNormalised.StartsWith(outerNormalised, StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(innerNormalised, outerNormalised, StringComparison.OrdinalIgnoreCase))
                        {
                            Api.Diagnostics.Record(
                                "LibraryOverlap.Detected",
                                string.Format(
                                    System.Globalization.CultureInfo.InvariantCulture,
                                    "Library '{0}' path '{1}' is inside library '{2}' path '{3}'. Overlapping libraries produce duplicate metadata, double-counted stats, and phantom Ungrouped / Misplaced findings. Split the parent library, narrow it, or remove one of the two in Dashboard → Libraries.",
                                    inner.Name,
                                    innerPath,
                                    outer.Name,
                                    outerPath));
                        }
                    }
                }
            }
        }

        var scanIsScoped = false;
        var enabledLibraries = runConfig.EnabledLibraries;
        if (enabledLibraries.Length > 0)
        {
            scanIsScoped = true;
            var idLookup = Scanners.VirtualFolderIdentity.BuildIdLookup(_libraryManager);
            var enabledLocations = _libraryManager.GetVirtualFolders()
                .Where(f => enabledLibraries.Contains(Scanners.VirtualFolderIdentity.GetId(f, idLookup), StringComparer.OrdinalIgnoreCase))
                .SelectMany(f => f.Locations)
                .Select(l => System.IO.Path.TrimEndingDirectorySeparator(l) + System.IO.Path.DirectorySeparatorChar)
                .ToList();
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && enabledLocations.Any(l => i.Path.StartsWith(l, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        // NFS/SMB safety net: a hard-mounted network share whose server is offline will hang every
        // syscall — File.Exists, FileInfo, ffprobe, all of it — indefinitely. Without this pre-flight,
        // the scanners would fan out thousands of stats against the dead mount, exhaust the ThreadPool,
        // and take the whole Jellyfin instance down (users report LXC restarts, GitHub issue class:
        // "scan locks up Jellyfin"). Probe each library root once with a short timeout; drop items on
        // any unreachable root so the scan finishes cleanly.
        // ponytail: hard-coded 5s timeout, add a config knob if users report false positives on
        // slow-but-reachable storage.
        var unreachableRoots = await FindUnreachableRootsAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (unreachableRoots.Count > 0)
        {
            var before = items.Count;
            items = items.Where(i => !string.IsNullOrEmpty(i.Path)
                && !unreachableRoots.Any(r => i.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            foreach (var root in unreachableRoots)
            {
                var msg = "Library folder '" + root.TrimEnd(System.IO.Path.DirectorySeparatorChar) + "' did not respond within 5s. Items under it were skipped this scan. If it's an NFS/SMB share, remount with soft,timeo=30,retrans=3 so a stalled server doesn't block Jellyfin.";
                _logger.LogWarning("ScanTask skipping unreachable library root: {Root}", root);
                Api.Diagnostics.Record("ScanTask.UnreachableRoot", msg);
            }

            _logger.LogInformation("Skipped {Skipped} item(s) under {Count} unreachable root(s).", before - items.Count, unreachableRoots.Count);
        }

        var scannedPaths = scanIsScoped
            ? items.SelectMany(MediaFileHelper.GetFilePaths).ToList()
            : null;
        // Reset the doomed-file set at scan start so the previous run's flags don't leak in.
        Plugin.ClearDoomed();

        // Honour FixMode.Off: the enum's own docstring is "The scanner does not run for this fix type."
        // Without this filter every registered scanner ran on every scheduled scan regardless of the
        // user's settings — field reports of PlayabilityScanner / AudioLanguageScanner chewing CPU
        // while the user had only EmbeddedCoverArt enabled trace to here.
        // MD-008: a scanner runs when ANY of its produced types is enabled, not just its primary
        // one. Otherwise a multi-type scanner (TranscodeLogScanner produces both HeavyTranscode
        // and FailedTranscode) is skipped whenever the primary type is Off — silently disabling
        // the secondary feature.
        var scanners = _scanners.Where(s => AnyProducedTypeEnabled(s, runConfig)).ToList();

        // Targeted-rescan filter (issue #64). ConfigChangeRescanner arms this via RunState so a
        // single-setting edit only re-runs the scanner(s) that read that setting, not the whole
        // library sweep. Consumed once — a scheduled sweep after the config edit runs everything.
        var typeFilter = _runState.ConsumeNextScanTypeFilter();
        if (typeFilter is not null && typeFilter.Count > 0)
        {
            scanners = scanners
                .Where(s => typeFilter.Contains(s.Type) || s.AdditionalTypes.Any(t => typeFilter.Contains(t)))
                .ToList();
            _logger.LogInformation("MediaDash scan narrowed to {Count} scanner(s) via NextRunTypeFilter: {Types}", scanners.Count, string.Join(", ", typeFilter));
        }

        _logger.LogInformation("MediaDash scan starting: {ItemCount} items, {ScannerCount} scanners ({SkippedCount} skipped as Off)", items.Count, scanners.Count, _scanners.Count() - scanners.Count);
        // Publish the run counts so the dashboard can render "N / M scanners done" alongside the
        // progress bar (same shape as fix runs' "N / M items left"). Cleared to null in the finally
        // below so the caption doesn't linger after the run finishes.
        Plugin.ScanScannersTotal = scanners.Count;
        Plugin.ScanScannersDone = 0;
        try
        {
            for (var i = 0; i < scanners.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var scanner = scanners[i];
                var baseProgress = i * 100.0 / scanners.Count;
                var slice = 100.0 / scanners.Count;
                var scannerProgress = new Progress<double>(p => progress.Report(baseProgress + (p * slice / 100.0)));

                // Coarse label for scanners that don't set per-file activity (Duplicate, MediaGrouper,
                // Nfo, Artwork, StaleContent, …). ProbingScannerBase-derived scanners overwrite this
                // per file with the same class name, so no flicker.
                Plugin.CurrentActivityLabel = scanner.GetType().Name;
                Plugin.CurrentActivity = null;

                // MD-006: per-scanner exception boundary. ProbingScannerBase already isolates
                // per-file probe failures, but a scanner-level exception (host-API drift, network
                // parser blowing up, LINQ NRE in an aggregation step) used to escape here and
                // abort the whole scan — every later scanner skipped, their prior Detected rows
                // left stale for another cycle. Now: rethrow cancellation, diagnose everything
                // else with the scanner's type + message, keep the earlier scanners' results
                // (we don't touch the DB when we didn't get issues), and move on.
                IReadOnlyList<Data.Issue>? issues = null;
                try
                {
                    issues = await scanner.ScanAsync(items, scannerProgress, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "MediaDash scanner {Type} failed; continuing with remaining scanners.", scanner.Type);
                    Api.Diagnostics.Record(
                        "ScanTask." + scanner.GetType().Name + ".Failed",
                        "Scanner " + scanner.GetType().Name + " (" + scanner.Type + ") threw " + ex.GetType().Name + ": " + ex.Message + ". Its prior Detected rows are left in place; the next scan will retry. Every other scanner in this run continues normally.");
                    Plugin.ScanScannersDone = i + 1;
                    continue;
                }

                // Feed the doomed-file set from scanners whose fix deletes the file, so later
                // probing scanners can skip it. Duplicate (loser copies) is by far the biggest
                // saver — a library with many dupes was previously ffprobing + thorough-decoding
                // both copies before the fixer deleted one.
                if (scanner.Type is Data.IssueType.Duplicate or Data.IssueType.MalwareRisk or Data.IssueType.OrphanedDebris)
                {
                    foreach (var issue in issues)
                    {
                        Plugin.MarkDoomed(issue.Path);
                    }
                }

                // Scanners that emit non-video-file paths (orphan folders, trickplay dirs, subtitle sidecars)
                // opt out of the scoped-delete branch — otherwise stale rows sit in the DB forever when the
                // user has EnabledLibraries set, because scannedPaths only contains video file paths.
                var pathsForReplace = scanner.AlwaysUnscoped ? null : scannedPaths;

                // MD-008: group by Issue.Type and persist each group under its own type so
                // multi-type scanners (TranscodeLogScanner) don't misclassify secondary rows as
                // the primary type. Also replaces the primary type's rows even when the scanner
                // emits nothing of that type this run (empty list = "no findings, wipe stale").
                var groupedByType = issues.GroupBy(iss => iss.Type).ToDictionary(g => g.Key, g => (IReadOnlyList<Data.Issue>)g.ToList());
                var typesToReplace = new List<Data.IssueType> { scanner.Type };
                typesToReplace.AddRange(scanner.AdditionalTypes);

                var persistFailed = false;
                foreach (var typeToReplace in typesToReplace)
                {
                    var groupIssues = groupedByType.TryGetValue(typeToReplace, out var g) ? g : (IReadOnlyList<Data.Issue>)System.Array.Empty<Data.Issue>();
                    try
                    {
                        _db.ReplaceDetectedIssues(typeToReplace, groupIssues, pathsForReplace);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Persisting scanner {Type} results failed; retaining prior Detected rows.", typeToReplace);
                        Api.Diagnostics.Record(
                            "ScanTask." + scanner.GetType().Name + ".PersistFailed",
                            "Failed to persist scanner " + scanner.GetType().Name + " (" + typeToReplace + ") results: " + ex.GetType().Name + ": " + ex.Message + ". Prior Detected rows for this type are retained; next successful scan will refresh them.");
                        persistFailed = true;
                        break;
                    }
                }

                if (persistFailed)
                {
                    Plugin.ScanScannersDone = i + 1;
                    continue;
                }

                _logger.LogInformation("MediaDash scanner {Type} found {Count} issues", scanner.Type, issues.Count);
                Plugin.ScanScannersDone = i + 1;
            }
        }
        finally
        {
            Plugin.CurrentActivity = null;
            Plugin.CurrentActivityLabel = null;
            Plugin.ScanScannersDone = null;
            Plugin.ScanScannersTotal = null;
        }

        // Refresh the redownload-warning list. Compares each recent successful re-encode against the
        // file currently at the same path — if the file is close to the size of the original still in
        // the recycle bin, something replaced our shrunk copy (Sonarr/Radarr redownload, or a manual
        // restore). Cheap: at most a couple stats per recent history row.
        try
        {
            Plugin.RedownloadWarnings = Api.RedownloadDetector.Detect(_db.History, TimeSpan.FromDays(30));
            if (Plugin.RedownloadWarnings.Count > 0)
            {
                _logger.LogInformation("MediaDash detected {Count} file(s) that appear to have been replaced after a successful re-encode.", Plugin.RedownloadWarnings.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Redownload detection failed.");
            Api.Diagnostics.Record("ScanTask.RedownloadDetect", "Redownload detection failed: " + ex.Message + ". Recycle-bin redownload warnings will be stale until the next successful scan.");
        }

        // Grant the fix-task grace period ONLY when this was a user click. Scheduled scans don't
        // set the timestamp — there's no user to shield from an opportunistic fix run.
        if (bypassIdleCheck)
        {
            _runState.RecordManualScanCompleted();
        }

        progress.Report(100);
    }

    // Enumerates every library root and races a Directory.Exists() call against a timeout.
    // Returns any root whose syscall didn't return in time (i.e. mount is hung). The probe thread
    // is orphaned if the syscall never returns — bounded to one leaked worker per unreachable root
    // per scan, which is finite in practice (users don't have many broken mounts at once).
    private async Task<List<string>> FindUnreachableRootsAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var roots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => System.IO.Path.TrimEndingDirectorySeparator(l) + System.IO.Path.DirectorySeparatorChar)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unreachable = new List<string>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await ProbeReachableAsync(root, timeout, cancellationToken).ConfigureAwait(false))
            {
                unreachable.Add(root);
            }
        }

        return unreachable;
    }

    // True if Directory.Exists returned within timeout (regardless of its bool result — a legitimately
    // missing root is a different problem the scanners already handle). False only means "syscall
    // hung past deadline", which is the NFS-lockup signature.
    internal static async Task<bool> ProbeReachableAsync(string root, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var probe = Task.Run(() =>
        {
            try
            {
                System.IO.Directory.Exists(root);
                return true;
            }
            catch
            {
                // Any thrown exception (unauthorized, invalid path, etc.) still means the syscall
                // returned in time — pass; the scanners will surface the real problem downstream.
                return true;
            }
        });

        var winner = await Task.WhenAny(probe, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);
        return winner == probe;
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // Midnight local time. The daily rescan only needs to catch a day's worth of change since
        // the last scan, so a single overnight sweep is enough; the idle-check gate keeps it from
        // fighting an actual viewer if one appears at 00:00. Users can still hit Scan now for a
        // manual pass, or edit the trigger from Dashboard → Scheduled Tasks.
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.Zero.Ticks
            }
        ];
    }

    // MD-008: returns true if the scanner's primary type OR any AdditionalTypes are enabled.
    // A multi-type scanner (TranscodeLogScanner produces both HeavyTranscode and FailedTranscode)
    // must run when either mode is set, otherwise setting the primary type to Off silently
    // disables the secondary feature.
    private static bool AnyProducedTypeEnabled(Scanners.IScanner scanner, Configuration.PluginConfiguration config)
    {
        if (config.GetFixMode(scanner.Type) != Configuration.FixMode.Off)
        {
            return true;
        }

        foreach (var extraType in scanner.AdditionalTypes)
        {
            if (config.GetFixMode(extraType) != Configuration.FixMode.Off)
            {
                return true;
            }
        }

        return false;
    }
}
