using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Xunit;

// VirtualFolderInfo.CollectionType is CollectionTypeOptions (MediaBrowser.Model),
// not the Jellyfin.Data.Enums.CollectionType returned by ILibraryCollectionResolver.
using CollectionTypeOptions = MediaBrowser.Model.Entities.CollectionTypeOptions;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

/// <summary>
/// Unit tests for <see cref="LibraryCollectionResolver"/>.
/// Uses the internal Func constructor to inject folder data without stubbing ILibraryManager.
/// </summary>
public class LibraryCollectionResolverTests
{
    private static LibraryCollectionResolver Resolver(params VirtualFolderInfo[] folders)
        => new(() => folders);

    private static BaseItem Item(string path) => new FakeItem(path);

    [Fact]
    public void ReturnsBooks_WhenPathUnderBooksLocation()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\media\books"],
            CollectionType = CollectionTypeOptions.books,
        });

        var result = resolver.Resolve(Item(@"C:\media\books\mybook.mobi"));

        Assert.Equal(CollectionType.books, result);
    }

    [Fact]
    public void ReturnsMovies_WhenPathUnderMoviesLocation()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\media\movies"],
            CollectionType = CollectionTypeOptions.movies,
        });

        var result = resolver.Resolve(Item(@"C:\media\movies\film.mkv"));

        Assert.Equal(CollectionType.movies, result);
    }

    [Fact]
    public void ReturnsNull_WhenPathOutsideAllLocations()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\media\books"],
            CollectionType = CollectionTypeOptions.books,
        });

        var result = resolver.Resolve(Item(@"C:\other\file.txt"));

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenCollectionTypeIsNull()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\media\misc"],
            CollectionType = null,
        });

        var result = resolver.Resolve(Item(@"C:\media\misc\file.epub"));

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenCollectionTypeIsMixed()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\media\mixed"],
            CollectionType = CollectionTypeOptions.mixed,
        });

        var result = resolver.Resolve(Item(@"C:\media\mixed\file.epub"));

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenItemPathIsEmpty()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\media\books"],
            CollectionType = CollectionTypeOptions.books,
        });

        var result = resolver.Resolve(Item(string.Empty));

        Assert.Null(result);
    }

    [Fact]
    public void MatchesCorrectLibraryWhenMultipleExist()
    {
        var resolver = Resolver(
            new VirtualFolderInfo { Locations = [@"C:\media\books"], CollectionType = CollectionTypeOptions.books },
            new VirtualFolderInfo { Locations = [@"C:\media\movies"], CollectionType = CollectionTypeOptions.movies });

        Assert.Equal(CollectionType.books, resolver.Resolve(Item(@"C:\media\books\thing.epub")));
        Assert.Equal(CollectionType.movies, resolver.Resolve(Item(@"C:\media\movies\film.mkv")));
    }

    [Fact]
    public void IsCaseInsensitive()
    {
        var resolver = Resolver(new VirtualFolderInfo
        {
            Locations = [@"C:\Media\Books"],
            CollectionType = CollectionTypeOptions.books,
        });

        // Different casing in item path — should still match (OrdinalIgnoreCase).
        var result = resolver.Resolve(Item(@"C:\media\books\mybook.epub"));

        Assert.Equal(CollectionType.books, result);
    }

    private sealed class FakeItem : BaseItem
    {
        public FakeItem(string path) { Path = path; }
    }
}
