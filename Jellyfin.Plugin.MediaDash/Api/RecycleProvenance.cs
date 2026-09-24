namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Where the bin item's metadata came from — determines what the UI can promise about restore.
/// </summary>
public enum RecycleProvenance
{
    /// <summary>
    /// A HistoryEntry row references this file — the fix run that recycled it is fully recorded
    /// (issue type, action verbatim, timestamp). Restore is a one-click operation.
    /// </summary>
    History = 0,

    /// <summary>
    /// No HistoryEntry, but the batch's origin manifest sidecar remembers the source path. Happens
    /// for user-initiated Files-tab deletes (which don't fill History) and for any recycle whose
    /// history row was later cleared. Restore still works via BinPath.
    /// </summary>
    Manifest = 1,

    /// <summary>
    /// Neither HistoryEntry nor manifest — recycled by a pre-manifest MediaDash build. The user
    /// has to use the Files tab's Recycle bin shortcut to move the file back manually.
    /// </summary>
    Orphan = 2,
}
