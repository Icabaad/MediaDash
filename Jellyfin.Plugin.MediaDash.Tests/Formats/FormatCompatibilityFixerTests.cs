using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MediaDash.Configuration;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Jellyfin.Plugin.MediaDash.Fixers.Converters;
using Jellyfin.Plugin.MediaDash.Scanners;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatCompatibilityFixerTests
{
    [Fact]
    public void CanFixReturnsTrueForFormatCompatibility()
    {
        var (fixer, _, _) = Setup();
        Assert.True(fixer.CanFix(IssueType.FormatCompatibility));
        Assert.False(fixer.CanFix(IssueType.Duplicate));
    }

    [Fact]
    public async Task RefusesWhenSourceMissing()
    {
        var (fixer, _, _) = Setup();
        var issue = MakeIssue(@"C:\nonexistent-mediadash-test\file.mobi");
        var result = await fixer.FixAsync(issue, null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Contains("missing", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefusesInMixedLibraryWithoutApproval()
    {
        var source = MakeTempFile(".mobi");
        try
        {
            // StubConverter "ebook" not registered → hits ConverterUnavailable before mixed check.
            // Register it so the test reaches the mixed-library gate.
            var (fixer, _, registry) = Setup();
            registry.Register(new StubConverter("ebook", MediaKind.Ebook, new CollectionType?[] { CollectionType.books }));
            var issue = MakeIssue(source.FullName, mixedLibrary: true);
            // getItemById returns null → isMixed = true
            var result = await fixer.FixAsync(issue, null, CancellationToken.None);
            Assert.False(result.Success);
            Assert.Contains("mixed", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(source.FullName); } catch { }
        }
    }

    [Fact]
    public async Task DryRunProducesPreview()
    {
        var source = MakeTempFile(".mobi");
        try
        {
            var itemId = Guid.NewGuid();
            var fakeItem = new FakeItem(itemId, source.FullName);

            var (fixer, resolver, registry) = Setup(
                dryRun: true,
                getItemById: id => id == itemId ? fakeItem : null);

            registry.Register(new StubConverter("ebook", MediaKind.Ebook, new CollectionType?[] { CollectionType.books }));
            resolver.Assign(fakeItem, CollectionType.books);

            var issue = MakeIssue(source.FullName, itemId: itemId);
            var result = await fixer.FixAsync(issue, null, CancellationToken.None);
            Assert.True(result.WasDryRun);
            Assert.Contains("Preview", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try { File.Delete(source.FullName); } catch { }
        }
    }

    // -- helpers --

    private static Issue MakeIssue(string path, bool mixedLibrary = false, Guid? itemId = null)
        => new()
        {
            Id = 42,
            Type = IssueType.FormatCompatibility,
            ItemId = itemId ?? Guid.NewGuid(),
            Path = path,
            DetailsJson = JsonSerializer.Serialize(new
            {
                sourceExt = Path.GetExtension(path),
                targetExt = ".epub",
                converter = "ebook",
                mixedLibrary
            }),
            SuggestedFix = "Convert",
            Status = IssueStatus.Detected
        };

    private static (FormatCompatibilityFixer fixer, StubResolver resolver, StubRegistry registry) Setup(
        bool dryRun = false,
        Func<Guid, BaseItem?>? getItemById = null)
    {
        // ponytail: NotImplementedException here is the RED step — Task 10 GREEN wires it.
        throw new NotImplementedException("FormatCompatibilityFixer construction — resolved in Task 10 GREEN step.");
    }

    private static FileInfo MakeTempFile(string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), "mediadash-fixer-" + Guid.NewGuid() + ext);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });
        return new FileInfo(path);
    }

    private sealed class StubConverter : IFormatConverter
    {
        public StubConverter(string key, MediaKind kind, CollectionType?[] allowed)
        {
            Key = key;
            Kind = kind;
            AllowedLibraryTypes = new HashSet<CollectionType?>(allowed);
        }

        public string Key { get; }
        public MediaKind Kind { get; }
        public IReadOnlySet<CollectionType?> AllowedLibraryTypes { get; }
        public ConverterAvailability Availability => ConverterAvailability.Ready;
        public bool CanConvert(string sourceExtension) => true;
        public Task<ConverterAvailability> EnsureToolchainAsync(CancellationToken ct) => Task.FromResult(ConverterAvailability.Ready);
        public Task<ConversionResult> ConvertAsync(string sourcePath, string stagedTargetPath, IProgress<double>? progress, CancellationToken ct)
        {
            File.WriteAllBytes(stagedTargetPath, new byte[] { 5, 6, 7, 8 });
            return Task.FromResult(new ConversionResult(true, stagedTargetPath, string.Empty));
        }
    }

    private sealed class StubRegistry : IFormatConverterRegistry
    {
        private readonly Dictionary<string, IFormatConverter> _map = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<IFormatConverter> All => _map.Values;
        public IFormatConverter? Get(string key) => _map.TryGetValue(key, out var c) ? c : null;
        public void Register(IFormatConverter c) => _map[c.Key] = c;
    }

    private sealed class StubResolver : ILibraryCollectionResolver
    {
        private readonly Dictionary<BaseItem, CollectionType?> _map = new();
        public void Assign(BaseItem item, CollectionType? type) => _map[item] = type;
        public CollectionType? Resolve(BaseItem item) => _map.TryGetValue(item, out var t) ? t : null;
    }

    private sealed class FakeItem : BaseItem
    {
        public FakeItem(Guid id, string path) { Id = id; Path = path; }
    }
}
