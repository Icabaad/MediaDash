namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Immutable snapshot of a single <see cref="RunState"/> slot at read time. The status endpoint
/// uses this to render the dashboard without holding the state lock during JSON serialisation.
/// </summary>
/// <param name="IsRunning">True if the slot is currently between <c>TryBegin</c> and <c>Complete</c>.</param>
/// <param name="CurrentPath">Best-effort file path the slot's task is currently working on, or null when idle / label-only.</param>
/// <param name="CurrentLabel">Best-effort human label ("DuplicateScanner", "TrackFixer") or null when idle.</param>
/// <param name="Processed">Progress numerator. Scan: scanners done. Fix: items processed. 0 when idle.</param>
/// <param name="Total">Progress denominator. Scan: scanners total. Fix: items total. 0 when idle.</param>
/// <param name="PauseReason">Human-readable reason the slot is paused, or null when running normally. Sticky after run end so the dashboard can explain why a run stopped early.</param>
public sealed record RunSnapshot(
    bool IsRunning,
    string? CurrentPath,
    string? CurrentLabel,
    int Processed,
    int Total,
    string? PauseReason);
