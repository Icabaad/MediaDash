using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Resolves the CollectionType of the CollectionFolder that owns a given BaseItem.
/// Wrapped as an interface so the format scanner can be unit-tested without a live Jellyfin instance.
/// </summary>
public interface ILibraryCollectionResolver
{
    /// <summary>Returns the CollectionType, or null when it can't be resolved (mixed / not in any library).</summary>
    /// <param name="item">The library item to inspect.</param>
    /// <returns>The library's CollectionType, or null.</returns>
    CollectionType? Resolve(BaseItem item);

    /// <summary>
    /// Path-only overload for files Jellyfin hasn't indexed (no BaseItem). Used by
    /// FormatCompatibilityFixer so unindexed files pick up the <em>current</em> library type at
    /// fix time rather than the scan-time snapshot in DetailsJson — a user who reclassifies a
    /// Books library to mixed before approving a fix would otherwise bypass the mixed-library
    /// gate (external review #4).
    /// </summary>
    /// <param name="path">Absolute file path.</param>
    /// <returns>The library's CollectionType, or null when path is outside every library or the owning library is mixed.</returns>
    CollectionType? ResolveByPath(string path);
}
