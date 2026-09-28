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

    private static (FormatCompatibilityScanner scanner, FakeResolver resolver) Scanner(params IFormatConverter[] converters)
    {
        var resolver = new FakeResolver();
        var registry = new FormatConverterRegistry(converters);
        var scanner = new FormatCompatibilityScanner(registry, resolver, NullLogger<FormatCompatibilityScanner>.Instance);
        return (scanner, resolver);
    }

    [Fact]
    public void EmitsIssueForUnrenderableExtension()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.mobi");
        resolver.Assign(item, CollectionType.books);
        var issues = Run(scanner, item);
        var issue = Assert.Single(issues);
        Assert.Equal(IssueType.FormatCompatibility, issue.Type);
        Assert.Equal(item.Path, issue.Path);
        Assert.Contains("EPUB", issue.SuggestedFix);
    }

    [Fact]
    public void SkipsRenderableExtensions()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.epub");
        resolver.Assign(item, CollectionType.books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsWhenLibraryTypeOutsideConverterAllowedSet()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\movies\thing.mobi");
        resolver.Assign(item, CollectionType.movies);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void EmitsIssueInMixedLibraryButFlagsUserReviewOnly()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\mixed\thing.mobi");
        resolver.Assign(item, null);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Contains("mixed", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkipsWhenNoConverterKnowsTheExtension()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("music", MediaKind.Music, CollectionType.music));
        var item = new FakeBaseItem(@"C:\books\thing.mobi");
        resolver.Assign(item, CollectionType.books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsUnknownExtensions()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.xyz");
        resolver.Assign(item, CollectionType.books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SetsIssueStatusToDetected()
    {
        var (scanner, resolver) = Scanner(new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.mobi");
        resolver.Assign(item, CollectionType.books);
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

    private sealed class FakeResolver : ILibraryCollectionResolver
    {
        private readonly Dictionary<BaseItem, CollectionType?> _map = new();

        public void Assign(BaseItem item, CollectionType? type)
        {
            _map[item] = type;
        }

        public CollectionType? Resolve(BaseItem item)
            => _map.TryGetValue(item, out var t) ? t : null;
    }

    private sealed class FakeBaseItem : BaseItem
    {
        public FakeBaseItem(string path)
        {
            Path = path;
        }
    }
}
