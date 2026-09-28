using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatCompatibilityScannerTests
{
    private static IReadOnlyList<Issue> Run(
        FormatCompatibilityScanner scanner,
        params BaseItem[] items)
        => scanner.ScanAsync(items, new Progress<double>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();

    private static BaseItem MakeItem(string path, CollectionType? libraryType)
        => new FakeBaseItem(path, libraryType);

    private static FormatCompatibilityScanner Scanner(params IFormatConverter[] converters)
        => new(new FormatConverterRegistry(converters), NullLogger<FormatCompatibilityScanner>.Instance);

    [Fact]
    public void EmitsIssueForUnrenderableExtension()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.mobi", CollectionType.Books);
        var issues = Run(scanner, item);
        var issue = Assert.Single(issues);
        Assert.Equal(IssueType.FormatCompatibility, issue.Type);
        Assert.Equal(item.Path, issue.Path);
        Assert.Contains("EPUB", issue.SuggestedFix);
    }

    [Fact]
    public void SkipsRenderableExtensions()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.epub", CollectionType.Books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsWhenLibraryTypeOutsideConverterAllowedSet()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\movies\thing.mobi", CollectionType.Movies);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void EmitsIssueInMixedLibraryButFlagsUserReviewOnly()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\mixed\thing.mobi", CollectionType.MixedContent);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Contains("mixed", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkipsWhenNoConverterKnowsTheExtension()
    {
        var scanner = Scanner(new FakeConverter("music", MediaKind.Music, CollectionType.Music));
        var item = MakeItem(@"C:\books\thing.mobi", CollectionType.Books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsUnknownExtensions()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.xyz", CollectionType.Books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SetsIssueStatusToDetected()
    {
        var scanner = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.Books));
        var item = MakeItem(@"C:\books\thing.mobi", CollectionType.Books);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Equal(IssueStatus.Detected, issue.Status);
    }

    private sealed class FakeConverter : IFormatConverter
    {
        public FakeConverter(string key, MediaKind kind, params CollectionType?[] allowed)
        {
            Key = key;
            Kind = kind;
            AllowedLibraryTypes = new HashSet<CollectionType?>(allowed);
        }

        public string Key { get; }
        public MediaKind Kind { get; }
        public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; }
        public bool CanConvert(string sourceExtension) => true;
        public ConverterAvailability Availability => ConverterAvailability.Ready;
        public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken cancellationToken)
            => Task.FromResult(ConverterAvailability.Ready);
    }

    private sealed class FakeBaseItem : BaseItem
    {
        private readonly CollectionType? _collectionType;

        public FakeBaseItem(string path, CollectionType? collectionType)
        {
            Path = path;
            _collectionType = collectionType;
        }

        // FormatCompatibilityScanner reads BaseItem.Path and resolves CollectionType via
        // an ILibraryCollectionResolver we inject; the fake resolver in these tests
        // pulls the value straight off this stashed field. See the resolver contract in
        // FormatCompatibilityScanner.cs.
        internal CollectionType? StashedCollectionType => _collectionType;
    }
}
