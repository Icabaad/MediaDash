namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Which task's slot in <see cref="RunState"/> a caller is operating on. Scan and Fix are
/// independent — a scan slot being busy does not block the fix slot and vice versa. There is
/// no shared "busy" bool: each role owns its own <c>IsRunning</c> guarded by the same lock.
/// </summary>
public enum RunRole
{
    /// <summary>The scheduled scan (<see cref="ScanTask"/>).</summary>
    Scan,

    /// <summary>The scheduled fix (<see cref="FixTask"/>).</summary>
    Fix,
}
