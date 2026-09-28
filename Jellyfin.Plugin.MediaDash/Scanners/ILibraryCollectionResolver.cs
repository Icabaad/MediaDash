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
}
