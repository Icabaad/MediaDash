using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.ScheduledTasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Composes the payload behind <c>GET /MediaDash/Status</c>. Extracted from
/// <see cref="MediaDashController.GetStatus"/> in 05-C.7 per plan §05-C: move status/library/history
/// aggregate composition out of the controller where it mixes volume and DB lookups; keep DTO
/// mapping and auth/status handling in HTTP layer.
/// </summary>
/// <remarks>
/// The controller keeps ownership of routing + auth + <c>ActionResult&lt;T&gt;</c> wrapping;
/// this service owns the actual gathering (issue summary + history aggregates + drive scan +
/// SMART probe + recycle-bin contents + config reads + plugin static state). Every consumer of
/// <see cref="StatusResponse"/> ships through <see cref="BuildStatus"/> so the payload shape stays
/// centralised.
/// </remarks>
public sealed class StatusQueryService
{
    private readonly MediaDashDb _db;
    private readonly IssueStore _issues;
    private readonly HistoryStore _history;
    private readonly Fixers.RecycleBin _recycleBin;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly RunState _runState;

    /// <summary>Initializes a new instance of the <see cref="StatusQueryService"/> class.</summary>
    /// <param name="db">Plugin DB — kept for the RepairSummary stub which touches no table yet.</param>
    /// <param name="issues">Issue-table store (05-C.2), source of open-issue summary + auto-queueable count.</param>
    /// <param name="history">History-table store (05-C.3), source of lifetime aggregates + failed-count badge.</param>
    /// <param name="recycleBin">Recycle bin — GetEffectiveRoot + GetContents.</param>
    /// <param name="libraryManager">Jellyfin library manager, used to enumerate library root paths for drive classification.</param>
    /// <param name="taskManager">Jellyfin task manager, used to look up the ScanTask and FixTask workers for progress reporting.</param>
    /// <param name="runState">Shared run-state holder (02-A), used to surface the fix-pause reason.</param>
    public StatusQueryService(
        MediaDashDb db,
        IssueStore issues,
        HistoryStore history,
        Fixers.RecycleBin recycleBin,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        RunState runState)
    {
        _db = db;
        _issues = issues;
        _history = history;
        _recycleBin = recycleBin;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _runState = runState;
    }

    /// <summary>Builds the full <see cref="StatusResponse"/> payload for the dashboard.</summary>
    /// <returns>The status response.</returns>
    public StatusResponse BuildStatus()
    {
        var summary = _issues.GetSummary();
        var scanTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is ScanTask);
        var fixTask = _taskManager.ScheduledTasks.FirstOrDefault(w => w.ScheduledTask is FixTask);
        long freeDisk = 0, totalDisk = 0;
        var drives = new List<DriveUsage>();

        // Roots that host a library folder — used to mark library drives and to keep the aggregated
        // FreeDiskBytes/TotalDiskBytes fields scoped to what MediaDash actually cares about.
        // FindDriveForPath is the right resolver on both Windows AND Linux — Path.GetPathRoot
        // returns "/" for every Linux path, which would collapse every library folder to root
        // and never match any mount point (leaving all drives unmarked as library drives on Linux,
        // which suppressed the SMART probe for every library drive there).
        var libraryRoots = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations)
            .Select(l => Fixers.RecycleBin.FindDriveForPath(l))
            .Where(d => d is not null)
            .Select(d => Path.TrimEndingDirectorySeparator(d!.RootDirectory.FullName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var recycleDrive = Fixers.RecycleBin.FindDriveForPath(_recycleBin.GetEffectiveRoot());
        var recycleRoot = recycleDrive is null ? null : Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                // Fixed only — skip CDs, network shares (which may not be ready or may be huge remote mounts we
                // don't want to poll), and RAM disks. IsReady guards against USB drives that are attached but
                // not yet mounted.
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                var trimmedName = Path.TrimEndingDirectorySeparator(drive.Name);
                var isLibraryDrive = libraryRoots.Contains(trimmedName);
                var isRecycleDrive = recycleRoot is not null && string.Equals(trimmedName, recycleRoot, StringComparison.OrdinalIgnoreCase);
                if (isLibraryDrive)
                {
                    freeDisk += drive.AvailableFreeSpace;
                    totalDisk += drive.TotalSize;
                }

                var usage = new DriveUsage
                {
                    Root = drive.Name,
                    FreeBytes = drive.AvailableFreeSpace,
                    TotalBytes = drive.TotalSize,
                    IsLibraryDrive = isLibraryDrive,
                    IsRecycleBinDrive = isRecycleDrive
                };
                if (isLibraryDrive || isRecycleDrive)
                {
                    CopySmartFields(usage, Probing.SmartHealthProbe.Get(drive.Name));
                }

                drives.Add(usage);
            }
            catch (IOException ex)
            {
                Diagnostics.Record("SystemStats.DriveStat", "Could not read drive '" + drive.Name + "' for the Overview: " + ex.Message + ". Its free-space total will be missing until the next scan.");
            }
            catch (UnauthorizedAccessException ex)
            {
                Diagnostics.Record("SystemStats.DriveStat", "Access denied reading drive '" + drive.Name + "' for the Overview: " + ex.Message + ". Grant Jellyfin's user read permission on the drive root to include it.");
            }
        }

        // Docker overlay / btrfs subvolume / etc. often report as DriveType.Unknown and get filtered out
        // above. If the recycle bin lives on one of those, its drive wouldn't appear on the Overview and
        // the user would run out of space with no warning. Force-add it here.
        if (recycleDrive is not null && !drives.Any(d =>
            string.Equals(Path.TrimEndingDirectorySeparator(d.Root), recycleRoot, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var recycleUsage = new DriveUsage
                {
                    Root = recycleDrive.Name,
                    FreeBytes = recycleDrive.AvailableFreeSpace,
                    TotalBytes = recycleDrive.TotalSize,
                    IsLibraryDrive = false,
                    IsRecycleBinDrive = true
                };
                CopySmartFields(recycleUsage, Probing.SmartHealthProbe.Get(recycleDrive.Name));
                drives.Add(recycleUsage);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var config = Plugin.Instance!.Configuration;
        var queuedCount = _issues.GetIssues(status: IssueStatus.Queued).Count;
        var autoQueueableCount = _issues.GetIssues(status: IssueStatus.Detected)
            .Count(i => config.GetFixMode(i.Type) == Configuration.FixMode.Automatic);
        var binContents = SafeGetBinContents();

        return new StatusResponse
        {
            IsScanning = scanTask is not null && scanTask.State != TaskState.Idle,
            ScanProgress = scanTask?.CurrentProgress,
            IsFixing = fixTask is not null && fixTask.State != TaskState.Idle,
            FixProgress = fixTask?.CurrentProgress,
            OpenIssueTotal = summary.Sum(s => s.Count),
            FailedHistoryTotal = _history.GetFailedHistoryCount(config.HistoryHiddenBeforeUtcTicks),
            FreeDiskBytes = freeDisk,
            TotalDiskBytes = totalDisk,
            LastScanUtc = summary.Count > 0 ? summary.Max(s => s.NewestDetectedUtc) : null,
            TotalPotentialSavings = summary.Sum(s => s.PotentialSavings),
            LifetimeBytesReclaimed = _history.GetLifetimeBytesFreed(),
            LifetimeCounts = _history.GetLifetimeSummary().Select(s => new TypeCount
            {
                Type = s.Type.ToString(),
                Count = s.Count,
                PotentialSavings = s.PotentialSavings
            }).ToList(),
            Counts = summary.Select(s => new TypeCount
            {
                Type = s.Type.ToString(),
                Count = s.Count,
                PotentialSavings = s.PotentialSavings
            }).ToList(),
            PendingFixCount = queuedCount + autoQueueableCount,
            ScanScannersDone = Plugin.ScanScannersDone,
            ScanScannersTotal = Plugin.ScanScannersTotal,
            Drives = drives,
            CurrentActivity = Plugin.CurrentActivity,
            CurrentActivityLabel = Plugin.CurrentActivityLabel,
            DataDirectory = MediaDashDb.DataDirectory,
            // When the user has hidden the System Performance card, skip sampling entirely — no WMI
            // perf counter, no /proc read, no PDH GPU walk, no nvidia-smi spawn. Empty payload lets
            // the client render its placeholder chip without a special-case. Doubled-up polling was
            // the field report: users running Task Manager / btop / Grafana alongside MediaDash
            // didn't want the plugin re-sampling the same counters every 3 seconds.
            System = Plugin.Instance!.Configuration.ShowSystemPerformance
                ? SystemStats.Sample()
                : new SystemStats { SystemStatsAvailable = false },
            RecycleBinPath = _recycleBin.GetEffectiveRoot(),
            RecycleBinCrossVolume = ComputeRecycleBinCrossVolume(drives),
            RecycleBinBytes = binContents.SizeBytes,
            RecycleBinFileCount = binContents.FileCount,
            RecycleBinRetentionDays = config.RecycleBinRetentionDays,
            LastFixRun = Plugin.LastFixRun,
            FixPauseReason = _runState.ReadSnapshot(RunRole.Fix).PauseReason,
            RedownloadWarnings = Plugin.RedownloadWarnings,
            Repair = _db.GetRepairSummary(DateTime.UtcNow.AddDays(-30).Ticks)
        };
    }

    private (int FileCount, long SizeBytes) SafeGetBinContents()
    {
        try
        {
            return _recycleBin.GetContents();
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    // Flatten a Probing.SmartHealthResult onto a DriveUsage. Keeping the DTO flat (as opposed to nesting a
    // "smart" object) means older clients ignore new fields cleanly and the Overview JS reads one row.
    private static void CopySmartFields(DriveUsage usage, Probing.SmartHealthResult health)
    {
        usage.SmartHealth = health.Status.ToString().ToLowerInvariant();
        usage.SmartMessage = health.Message;
        usage.SmartModel = health.ModelName;
        usage.SmartTemperatureCelsius = health.TemperatureCelsius;
        usage.SmartTemperatureMaxCelsius = health.TemperatureMaxCelsius;
        usage.SmartWearPercent = health.WearPercent;
        usage.SmartPowerOnHours = health.PowerOnHours;
        usage.SmartReadErrorsUncorrected = health.ReadErrorsUncorrected;
        usage.SmartWriteErrorsUncorrected = health.WriteErrorsUncorrected;
    }

    private bool ComputeRecycleBinCrossVolume(List<DriveUsage> drives)
    {
        // Only warn when there ARE library drives (i.e., the user has configured libraries) and the recycle
        // bin sits on a different volume. On single-drive setups the answer is trivially "no" — do nothing.
        var libraryDrives = drives.Where(d => d.IsLibraryDrive).ToList();
        if (libraryDrives.Count == 0)
        {
            return false;
        }

        var recycleDrive = Fixers.RecycleBin.FindDriveForPath(_recycleBin.GetEffectiveRoot());
        if (recycleDrive is null)
        {
            return false;
        }

        var recycleRoot = Path.TrimEndingDirectorySeparator(recycleDrive.RootDirectory.FullName);
        return !libraryDrives.Any(d => string.Equals(
            Path.TrimEndingDirectorySeparator(d.Root),
            recycleRoot,
            StringComparison.OrdinalIgnoreCase));
    }
}
