using System.Linq;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

public class FormatMatrixConsistencyTests
{
    [Fact]
    public void AllRowsHaveNonEmptyExtension()
    {
        Assert.All(FormatMatrix.All, row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.Extension), $"row for {row.Kind} has empty extension");
            Assert.StartsWith(".", row.Extension);
            Assert.Equal(row.Extension.ToLowerInvariant(), row.Extension);
        });
    }

    [Fact]
    public void NoDuplicateExtensions()
    {
        var dupes = FormatMatrix.All.GroupBy(r => r.Extension).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(dupes);
    }

    [Fact]
    public void RenderableRowsHaveNoTarget()
    {
        Assert.All(
            FormatMatrix.All.Where(r => r.ClientRenders),
            row => Assert.Null(row.TargetExtension));
    }

    [Fact]
    public void RenderableRowsHaveConverterKeyNone()
    {
        Assert.All(
            FormatMatrix.All.Where(r => r.ClientRenders),
            row => Assert.Equal("none", row.ConverterKey));
    }

    [Fact]
    public void UnrenderableRowsWithTargetHaveConverterKey()
    {
        Assert.All(
            FormatMatrix.All.Where(r => !r.ClientRenders && r.TargetExtension is not null),
            row =>
            {
                Assert.False(string.IsNullOrWhiteSpace(row.ConverterKey));
                Assert.NotEqual("none", row.ConverterKey);
            });
    }

    [Fact]
    public void ClassifyKnownExtension()
    {
        var row = FormatMatrix.Classify("/some/path/book.mobi");
        Assert.NotNull(row);
        Assert.Equal(MediaKind.Ebook, row!.Kind);
        Assert.Equal(".mobi", row.Extension);
    }

    [Fact]
    public void ClassifyUnknownExtension()
    {
        Assert.Null(FormatMatrix.Classify("/some/path/whatever.xyz"));
    }

    [Fact]
    public void ClassifyIsCaseInsensitive()
    {
        Assert.NotNull(FormatMatrix.Classify("/BOOK.MOBI"));
        Assert.NotNull(FormatMatrix.Classify("/book.MoBi"));
    }
}
