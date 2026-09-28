using System;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>Production resolver — walks BaseItem.GetTopParent to reach the CollectionFolder.</summary>
public sealed class LibraryCollectionResolver : ILibraryCollectionResolver
{
    /// <summary>Initializes a new instance of the <see cref="LibraryCollectionResolver"/> class.</summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    public LibraryCollectionResolver(ILibraryManager libraryManager)
    {
        // ponytail: parked for Phase 2+ use — GetTopParent() does the walk itself in Phase 1.
        _ = libraryManager;
    }

    /// <inheritdoc />
    public CollectionType? Resolve(BaseItem item)
    {
        try
        {
            var top = item.GetTopParent();
            if (top is CollectionFolder cf)
            {
                return cf.CollectionType;
            }

            return null;
        }
        catch (InvalidOperationException)
        {
            // Item detached from the library tree; treat as unresolved.
            return null;
        }
    }
}
