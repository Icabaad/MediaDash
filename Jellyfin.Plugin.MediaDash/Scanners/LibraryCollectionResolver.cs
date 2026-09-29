using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Production resolver — path-prefix lookup against Jellyfin's virtual-folder list.
/// </summary>
/// <remarks>
/// Book items on Jellyfin 10.11 don't surface their <see cref="CollectionFolder"/> via
/// <c>GetTopParent()</c> — that walk climbs to <c>AggregateFolder</c> instead.
/// A path-prefix match against <c>ILibraryManager.GetVirtualFolders()</c> is the
/// reliable cross-type way to identify which library a file lives in.
/// </remarks>
public sealed class LibraryCollectionResolver : ILibraryCollectionResolver
{
    private readonly Func<IEnumerable<VirtualFolderInfo>> _getFolders;

    /// <summary>Initializes a new instance of the <see cref="LibraryCollectionResolver"/> class.</summary>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    public LibraryCollectionResolver(ILibraryManager libraryManager)
        : this(libraryManager.GetVirtualFolders)
    {
    }

    // ponytail: internal seam so tests inject folder data without stubbing all of ILibraryManager.
    internal LibraryCollectionResolver(Func<IEnumerable<VirtualFolderInfo>> getFolders)
    {
        _getFolders = getFolders;
    }

    /// <inheritdoc />
    public CollectionType? Resolve(BaseItem item)
    {
        var path = item.Path;
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var folder in _getFolders())
        {
            foreach (var location in folder.Locations ?? [])
            {
                if (!string.IsNullOrEmpty(location))
                {
                    // ponytail: append separator so "C:\media\books" doesn't false-positive on "C:\media\books_extended\...".
                    var locWithSep = location.EndsWith(Path.DirectorySeparatorChar) || location.EndsWith(Path.AltDirectorySeparatorChar)
                        ? location
                        : location + Path.DirectorySeparatorChar;
                    if (path.StartsWith(locWithSep, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToCollectionType(folder.CollectionType);
                    }
                }
            }
        }

        return null;
    }

    // VirtualFolderInfo uses CollectionTypeOptions (MediaBrowser.Model), while the scanner
    // contract uses CollectionType (Jellyfin.Data.Enums). Same lowercase names, different types.
    // CollectionTypeOptions.mixed means no specific type → return null.
    private static CollectionType? ToCollectionType(CollectionTypeOptions? raw)
    {
        if (raw is null || raw == CollectionTypeOptions.mixed)
        {
            return null;
        }

        return Enum.TryParse<CollectionType>(raw.ToString(), ignoreCase: true, out var parsed)
            ? parsed
            : null;
    }
}
