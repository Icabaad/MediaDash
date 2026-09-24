namespace Jellyfin.Plugin.MediaDash.Fixers;

// Which of the three restore routes is being requested. See <see cref="RestoreService"/>.
internal enum RestoreKind
{
    // POST /MediaDash/History/{id}/Restore — restores by history row id. Uses history.recycle_path
    // as the source. Supports force (destructive swap-and-restore).
    History,

    // POST /MediaDash/RedownloadWarnings/{historyId}/RestoreOptimized — restores the smaller "twin"
    // an old SubtitleLanguage bug moved to the bin. Uses FindOptimizedTwin to locate the source
    // in the bin at claim time. Supports force.
    OptimizedCopy,

    // POST /MediaDash/RecycleBin/Items/Restore — restores by bin path for manifest-only entries
    // (no history row). Uses RecycleBin.ListContents to look up the origin. No force.
    ManifestEntry,
}
