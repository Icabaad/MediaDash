using System.Collections.Generic;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Tests for the FormatCompatibility per-converter analytics grouping helpers.
/// All tests exercise the static <c>IssueStore.GroupByConverter</c> /
/// <c>IssueStore.ExtractConverterKey</c> helpers directly — no DB required.
/// </summary>
public sealed class FormatCompatConverterAnalyticsTests
{
    [Fact]
    public void GroupsFormatCompatibilityIssuesByConverter()
    {
        // 3 ebook + 2 comic issues → {ebook:3, comic:2}
        var details = new List<string>
        {
            "{\"converter\":\"ebook\",\"sourceExt\":\".mobi\",\"targetExt\":\".epub\"}",
            "{\"converter\":\"ebook\",\"sourceExt\":\".mobi\",\"targetExt\":\".epub\"}",
            "{\"converter\":\"ebook\",\"sourceExt\":\".azw3\",\"targetExt\":\".epub\"}",
            "{\"converter\":\"comic\",\"sourceExt\":\".cbr\",\"targetExt\":\".cbz\"}",
            "{\"converter\":\"comic\",\"sourceExt\":\".cbr\",\"targetExt\":\".cbz\"}"
        };

        var result = IssueStore.GroupByConverter(details);

        Assert.Equal(3, result["ebook"]);
        Assert.Equal(2, result["comic"]);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void GroupsFormatCompatibilityHistoryByConverter_CountsAndBytes()
    {
        // Mixed rows: image × 2 (100 bytes each), video × 1 (500 bytes)
        // Simulate the static helper's grouping logic on raw detail strings + byte values
        var rows = new List<(string Details, long Bytes)>
        {
            ("{\"converter\":\"image\",\"sourceExt\":\".heic\",\"targetExt\":\".jpg\"}", 100),
            ("{\"converter\":\"image\",\"sourceExt\":\".heic\",\"targetExt\":\".jpg\"}", 100),
            ("{\"converter\":\"video\",\"sourceExt\":\".rmvb\",\"targetExt\":\".mp4\"}", 500),
        };

        var fixedCounts = new System.Collections.Generic.Dictionary<string, int>();
        var bytesReclaimed = new System.Collections.Generic.Dictionary<string, long>();
        foreach (var (details, bytes) in rows)
        {
            var key = IssueStore.ExtractConverterKey(details);
            fixedCounts.TryGetValue(key, out var n);
            fixedCounts[key] = n + 1;
            bytesReclaimed.TryGetValue(key, out var b);
            bytesReclaimed[key] = b + bytes;
        }

        Assert.Equal(2, fixedCounts["image"]);
        Assert.Equal(200L, bytesReclaimed["image"]);
        Assert.Equal(1, fixedCounts["video"]);
        Assert.Equal(500L, bytesReclaimed["video"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"other\":\"field\"}")]
    [InlineData("{not valid json")]
    public void UnknownConverterBucketsAsUnknown(string? details)
    {
        var key = IssueStore.ExtractConverterKey(details);
        Assert.Equal("unknown", key);
    }

    [Fact]
    public void EmptyConverterStringBucketsAsUnknown()
    {
        var key = IssueStore.ExtractConverterKey("{\"converter\":\"\"}");
        Assert.Equal("unknown", key);
    }

    [Fact]
    public void MixedKnownAndUnknownRowsGroupCorrectly()
    {
        var details = new List<string>
        {
            "{\"converter\":\"music\"}",
            "{\"converter\":\"music\"}",
            "{}",                          // no converter field → unknown
            "{\"converter\":\"music\"}",
        };

        var result = IssueStore.GroupByConverter(details);

        Assert.Equal(3, result["music"]);
        Assert.Equal(1, result["unknown"]);
    }
}
