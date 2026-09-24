namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Tri-state outcome of a fix-time playability revalidation. MD-010: Indeterminate must never
/// lead to repair or disposal — it means ffprobe couldn't run and we don't actually know whether
/// the file is broken. Failing closed here protects healthy files from irreversible deletion when
/// ffprobe temporarily disappears (Jellyfin update, container mount hiccup, PATH change).
/// </summary>
internal enum RevalidationVerdict
{
    /// <summary>Revalidation confirmed the file still matches the scanner's flagged defect.</summary>
    Broken,

    /// <summary>Revalidation succeeded and the file no longer matches the flagged defect.</summary>
    Healthy,

    /// <summary>Revalidation couldn't be performed (probe unavailable, timeout, malformed output).</summary>
    Indeterminate
}
