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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatCompatibilityScannerTests
{
    private static IReadOnlyList<Issue> Run(
        FormatCompatibilityScanner scanner,
        params BaseItem[] items)
        => scanner.ScanAsync(items, new Progress<double>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();

    private static (FormatCompatibilityScanner scanner, FakeResolver resolver) Scanner(
        IEnumerable<VirtualFolderInfo>? folders = null,
        params IFormatConverter[] converters)
    {
        var resolver = new FakeResolver();
        var registry = new FormatConverterRegistry(converters);
        var folderList = folders?.ToList() ?? new List<VirtualFolderInfo>();
        var scanner = new FormatCompatibilityScanner(registry, resolver, () => folderList, NullLogger<FormatCompatibilityScanner>.Instance);
        return (scanner, resolver);
    }

    [Fact]
    public void EmitsIssueForUnrenderableExtension()
    {
        var (scanner, resolver) = Scanner(converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
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
        var (scanner, resolver) = Scanner(converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.epub");
        resolver.Assign(item, CollectionType.books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsWhenLibraryTypeOutsideConverterAllowedSet()
    {
        var (scanner, resolver) = Scanner(converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\movies\thing.mobi");
        resolver.Assign(item, CollectionType.movies);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void EmitsIssueInMixedLibraryButFlagsUserReviewOnly()
    {
        var (scanner, resolver) = Scanner(converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\mixed\thing.mobi");
        resolver.Assign(item, null);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Contains("mixed", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SkipsWhenNoConverterKnowsTheExtension()
    {
        var (scanner, resolver) = Scanner(converters: new FakeConverter("music", MediaKind.Music, CollectionType.music));
        var item = new FakeBaseItem(@"C:\books\thing.mobi");
        resolver.Assign(item, CollectionType.books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SkipsUnknownExtensions()
    {
        var (scanner, resolver) = Scanner(converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.xyz");
        resolver.Assign(item, CollectionType.books);
        Assert.Empty(Run(scanner, item));
    }

    [Fact]
    public void SetsIssueStatusToDetected()
    {
        var (scanner, resolver) = Scanner(converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));
        var item = new FakeBaseItem(@"C:\books\thing.mobi");
        resolver.Assign(item, CollectionType.books);
        var issue = Assert.Single(Run(scanner, item));
        Assert.Equal(IssueStatus.Detected, issue.Status);
    }

    [Fact]
    public void EmitsIssueForUnindexedFileInBooksLibrary()
    {
        // .txt is ServerIndexes=false — Jellyfin never creates a BaseItem for it, so the
        // normal item-walk misses it. The filesystem sweep must catch it.
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var docPath = Path.Combine(tmp, "poem.txt");
            File.WriteAllText(docPath, "The quick brown fox.");

            var folder = new VirtualFolderInfo
            {
                CollectionType = CollectionTypeOptions.books,
                Locations = new[] { tmp }
            };

            var (scanner, _) = Scanner(
                folders: new[] { folder },
                converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));

            // No BaseItems — pure filesystem sweep.
            var issues = Run(scanner);

            var issue = Assert.Single(issues);
            Assert.Equal(docPath, issue.Path, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Guid.Empty, issue.ItemId);
            Assert.Contains("unindexed", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("books", issue.DetailsJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(IssueStatus.Detected, issue.Status);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void UnindexedFilesInWrongLibraryTypeAreSkipped()
    {
        // An ebook converter only covers CollectionType.books.
        // A .txt inside a movies library should be skipped.
        var tmp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.WriteAllText(Path.Combine(tmp, "script.txt"), "action!");

            var folder = new VirtualFolderInfo
            {
                CollectionType = CollectionTypeOptions.movies,
                Locations = new[] { tmp }
            };

            var (scanner, _) = Scanner(
                folders: new[] { folder },
                converters: new FakeConverter("ebook", MediaKind.Ebook, CollectionType.books));

            Assert.Empty(Run(scanner));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
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

        public Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken cancellationToken)
            => Task.FromResult(new ConversionResult(false, null, "FakeConverter does not perform real conversions."));
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
