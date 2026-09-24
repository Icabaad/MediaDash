using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Owns every transient control flag a scan or fix run consults. Replaces the scattered
/// <c>static bool</c>s and <c>static string?</c>s that used to live on <see cref="Plugin"/>,
/// <see cref="ScanTask"/> and <see cref="FixTask"/> with one lock-guarded holder.
///
/// Design invariants:
/// <list type="bullet">
///   <item>Scan and Fix are independent slots — there is no shared "busy" bool. A scan and a fix
///     can be running simultaneously (Jellyfin's scheduler serialises them today but nothing here
///     depends on that).</item>
///   <item>Every mutation and every read is guarded by <c>_sync</c>. Operations are short (field
///     reads/writes), so contention is a non-issue.</item>
///   <item>One-shot bypass and next-scan-filter are consumed exactly once. A second consumer
///     observes the reset state — races between "arm" and "consume" are decided by lock order,
///     not by later reads.</item>
///   <item>Persistent summaries (LastFixRun, RedownloadWarnings, diagnostics) are NOT owned here —
///     those retain <see cref="Api.PluginState"/> write-through so a restart keeps the banner.</item>
/// </list>
/// </summary>
public sealed class RunState
{
    private readonly object _sync = new();
    private readonly TimeProvider _clock;
    private readonly Slot _scan = new();
    private readonly Slot _fix = new();

    private bool _scanBypassIdleOnce;
    private bool _fixBypassIdleOnce;
    private IReadOnlySet<Data.IssueType>? _nextScanTypeFilter;
    private bool _fixIgnoreActivity;
    private bool _lowImpactHintEmitted;
    private DateTime? _manualScanCompletedUtc;

    /// <summary>
    /// Initializes a new instance of the <see cref="RunState"/> class using the system clock.
    /// Public constructor used by DI.
    /// </summary>
    public RunState()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RunState"/> class with an explicit clock.
    /// Tests pass a fake <see cref="TimeProvider"/> to control the manual-scan grace window without
    /// wall-clock sleeps.
    /// </summary>
    /// <param name="clock">The clock used for <see cref="RecordManualScanCompleted"/>.</param>
    public RunState(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>Gets a value indicating whether the current fix run should ignore per-file activity checks.</summary>
    public bool FixIgnoreActivity
    {
        get
        {
            lock (_sync)
            {
                return _fixIgnoreActivity;
            }
        }
    }

    /// <summary>Tries to enter the role slot. Returns false when the slot is already running.</summary>
    /// <param name="role">Which slot to enter.</param>
    /// <returns>True when the caller may proceed with the run; false when the slot is busy.</returns>
    public bool TryBegin(RunRole role)
    {
        lock (_sync)
        {
            var slot = Get(role);
            if (slot.IsRunning)
            {
                return false;
            }

            slot.Reset();
            slot.IsRunning = true;
            if (role == RunRole.Fix)
            {
                // Match the pre-migration FixTask.ExecuteAsync (line 141): a new fix run clears the
                // ignore-activity flag from the previous run.
                _fixIgnoreActivity = false;
            }

            return true;
        }
    }

    /// <summary>Updates the running slot's progress and current-file label. No-op if the slot is not running.</summary>
    /// <param name="role">Which slot to update.</param>
    /// <param name="currentPath">The path currently being processed, or null for a label-only update.</param>
    /// <param name="currentLabel">The human label ("ScannerName" / "FixerName"), or null to clear.</param>
    /// <param name="processed">Numerator of the progress ratio.</param>
    /// <param name="total">Denominator of the progress ratio.</param>
    public void UpdateProgress(RunRole role, string? currentPath, string? currentLabel, int processed, int total)
    {
        lock (_sync)
        {
            var slot = Get(role);
            if (!slot.IsRunning)
            {
                return;
            }

            slot.CurrentPath = currentPath;
            slot.CurrentLabel = currentLabel;
            slot.Processed = processed;
            slot.Total = total;
        }
    }

    /// <summary>Sets or clears the pause reason for a role. Sticky after Complete so the dashboard can explain a run that stopped early.</summary>
    /// <param name="role">Which slot's pause banner.</param>
    /// <param name="reason">Human-readable reason, or null to clear.</param>
    public void SetPauseReason(RunRole role, string? reason)
    {
        lock (_sync)
        {
            Get(role).PauseReason = reason;
        }
    }

    /// <summary>Marks the role slot idle. Clears progress + current-path but preserves the sticky pause reason.</summary>
    /// <param name="role">Which slot to close.</param>
    public void Complete(RunRole role)
    {
        lock (_sync)
        {
            var slot = Get(role);
            slot.IsRunning = false;
            slot.CurrentPath = null;
            slot.CurrentLabel = null;
            slot.Processed = 0;
            slot.Total = 0;
            // PauseReason intentionally sticky — see class-level invariant + PluginState behaviour.
            if (role == RunRole.Fix)
            {
                _fixIgnoreActivity = false;
            }
        }
    }

    /// <summary>Returns the current view of a slot without holding the lock past return.</summary>
    /// <param name="role">Which slot to read.</param>
    /// <returns>A coherent snapshot of the slot's fields.</returns>
    public RunSnapshot ReadSnapshot(RunRole role)
    {
        lock (_sync)
        {
            var slot = Get(role);
            return new RunSnapshot(
                slot.IsRunning,
                slot.CurrentPath,
                slot.CurrentLabel,
                slot.Processed,
                slot.Total,
                slot.PauseReason);
        }
    }

    /// <summary>Arms the one-shot idle-check bypass for the next run of <paramref name="role"/>. Idempotent when already armed.</summary>
    /// <param name="role">Which slot's bypass to arm.</param>
    public void ArmBypassIdleCheck(RunRole role)
    {
        lock (_sync)
        {
            if (role == RunRole.Scan)
            {
                _scanBypassIdleOnce = true;
            }
            else
            {
                _fixBypassIdleOnce = true;
            }
        }
    }

    /// <summary>Reads and clears the one-shot idle-check bypass. Returns true when the caller should bypass the idle check.</summary>
    /// <param name="role">Which slot's bypass to consume.</param>
    /// <returns>True when the bypass was armed at read time.</returns>
    public bool ConsumeBypassIdleCheck(RunRole role)
    {
        lock (_sync)
        {
            if (role == RunRole.Scan)
            {
                var v = _scanBypassIdleOnce;
                _scanBypassIdleOnce = false;
                return v;
            }

            var w = _fixBypassIdleOnce;
            _fixBypassIdleOnce = false;
            return w;
        }
    }

    /// <summary>Arms the next-run type filter for a scan. Overwrites any pending filter.</summary>
    /// <param name="filter">Types the next scan restricts to, or null to clear.</param>
    public void ArmNextScanTypeFilter(IReadOnlySet<Data.IssueType>? filter)
    {
        lock (_sync)
        {
            _nextScanTypeFilter = filter;
        }
    }

    /// <summary>Reads and clears the pending scan type filter.</summary>
    /// <returns>The armed filter, or null when none was pending.</returns>
    public IReadOnlySet<Data.IssueType>? ConsumeNextScanTypeFilter()
    {
        lock (_sync)
        {
            var f = _nextScanTypeFilter;
            _nextScanTypeFilter = null;
            return f;
        }
    }

    /// <summary>Flag set from the dashboard's "Ignore activity" button so a paused fix run resumes despite a viewer.</summary>
    public void SetFixIgnoreActivity()
    {
        lock (_sync)
        {
            _fixIgnoreActivity = true;
        }
    }

    /// <summary>Records that a MANUAL scan just finished. Consumed by FixTask's grace-window check.</summary>
    public void RecordManualScanCompleted()
    {
        lock (_sync)
        {
            _manualScanCompletedUtc = _clock.GetUtcNow().UtcDateTime;
        }
    }

    /// <summary>Reads (without clearing) when the last manual scan finished. Null when no manual scan has completed this session.</summary>
    /// <returns>UTC timestamp of the last manual scan, or null.</returns>
    public DateTime? PeekManualScanCompleted()
    {
        lock (_sync)
        {
            return _manualScanCompletedUtc;
        }
    }

    /// <summary>Test-only: forces the manual-scan timestamp to a fixed value (or clears it).</summary>
    /// <param name="value">The timestamp to install, or null to clear.</param>
    internal void SetManualScanCompletedForTests(DateTime? value)
    {
        lock (_sync)
        {
            _manualScanCompletedUtc = value;
        }
    }

    /// <summary>Emits the "consider Low system impact mode" hint at most once per plugin lifetime.</summary>
    /// <returns>True when the caller should emit the hint (this call flipped the flag); false when already emitted.</returns>
    public bool TryEmitLowImpactHint()
    {
        lock (_sync)
        {
            if (_lowImpactHintEmitted)
            {
                return false;
            }

            _lowImpactHintEmitted = true;
            return true;
        }
    }

    /// <summary>Test-only: clears the low-impact-hint latch so per-case tests can re-exercise the flip.</summary>
    internal void ResetLowImpactHintForTests()
    {
        lock (_sync)
        {
            _lowImpactHintEmitted = false;
        }
    }

    /// <summary>
    /// Aggregator read for the display-only <c>Plugin.CurrentActivity</c> facade. Fix wins over
    /// Scan when both are running so a fix that steps in mid-scan produces a coherent readout on
    /// the dashboard (the fix's path is more actionable than the scan's).
    /// </summary>
    /// <returns>The current path from whichever slot is running, or null when both are idle.</returns>
    public string? GetAggregatedCurrentPath()
    {
        lock (_sync)
        {
            return _fix.IsRunning ? _fix.CurrentPath : _scan.CurrentPath;
        }
    }

    /// <summary>Aggregator read for <c>Plugin.CurrentActivityLabel</c>. Same precedence as <see cref="GetAggregatedCurrentPath"/>.</summary>
    /// <returns>The current label from whichever slot is running, or null when idle.</returns>
    public string? GetAggregatedCurrentLabel()
    {
        lock (_sync)
        {
            return _fix.IsRunning ? _fix.CurrentLabel : _scan.CurrentLabel;
        }
    }

    /// <summary>
    /// Legacy setter for the <c>Plugin.CurrentActivity</c> facade. Routes the write to whichever
    /// slot is currently running (Fix wins). No-op when both slots are idle — a stray late write
    /// from a finished run cannot corrupt the dashboard readout.
    /// </summary>
    /// <param name="value">The current path, or null.</param>
    public void SetCurrentPathFromLegacy(string? value)
    {
        lock (_sync)
        {
            if (_fix.IsRunning)
            {
                _fix.CurrentPath = value;
            }
            else if (_scan.IsRunning)
            {
                _scan.CurrentPath = value;
            }
        }
    }

    /// <summary>Legacy setter for <c>Plugin.CurrentActivityLabel</c>. Same routing as <see cref="SetCurrentPathFromLegacy"/>.</summary>
    /// <param name="value">The current label, or null.</param>
    public void SetCurrentLabelFromLegacy(string? value)
    {
        lock (_sync)
        {
            if (_fix.IsRunning)
            {
                _fix.CurrentLabel = value;
            }
            else if (_scan.IsRunning)
            {
                _scan.CurrentLabel = value;
            }
        }
    }

    /// <summary>Read for the <c>Plugin.ScanScannersDone</c> facade. Null when the scan slot is idle.</summary>
    /// <returns>The scan slot's <c>Processed</c> counter, or null when the slot is not running.</returns>
    public int? GetScanScannersDone()
    {
        lock (_sync)
        {
            return _scan.IsRunning ? _scan.Processed : (int?)null;
        }
    }

    /// <summary>Read for the <c>Plugin.ScanScannersTotal</c> facade. Null when the scan slot is idle.</summary>
    /// <returns>The scan slot's <c>Total</c> counter, or null when the slot is not running.</returns>
    public int? GetScanScannersTotal()
    {
        lock (_sync)
        {
            return _scan.IsRunning ? _scan.Total : (int?)null;
        }
    }

    /// <summary>Legacy setter for <c>Plugin.ScanScannersDone</c>. Writes to the scan slot when running; null is treated as a clear no-op.</summary>
    /// <param name="value">The new done count, or null.</param>
    public void SetScanScannersDoneFromLegacy(int? value)
    {
        lock (_sync)
        {
            if (_scan.IsRunning && value is int v)
            {
                _scan.Processed = v;
            }
        }
    }

    /// <summary>Legacy setter for <c>Plugin.ScanScannersTotal</c>. Same behaviour as <see cref="SetScanScannersDoneFromLegacy"/> on the total field.</summary>
    /// <param name="value">The new total, or null.</param>
    public void SetScanScannersTotalFromLegacy(int? value)
    {
        lock (_sync)
        {
            if (_scan.IsRunning && value is int v)
            {
                _scan.Total = v;
            }
        }
    }

    private Slot Get(RunRole role) => role == RunRole.Scan ? _scan : _fix;

    private sealed class Slot
    {
        public bool IsRunning { get; set; }

        public string? CurrentPath { get; set; }

        public string? CurrentLabel { get; set; }

        public int Processed { get; set; }

        public int Total { get; set; }

        public string? PauseReason { get; set; }

        public void Reset()
        {
            IsRunning = false;
            CurrentPath = null;
            CurrentLabel = null;
            Processed = 0;
            Total = 0;
            PauseReason = null;
        }
    }
}
