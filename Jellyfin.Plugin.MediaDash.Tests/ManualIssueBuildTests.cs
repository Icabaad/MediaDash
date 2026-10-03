using System;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public sealed class ManualIssueBuildTests
{
    private static readonly Guid ItemId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public void Build_Quality_SetsPathTypeAndEmptyDetails()
    {
        var req = new ManualIssueRequest { Path = "/lib/a.mkv", Type = ManualIssueType.Quality };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.NotNull(issue);
        Assert.Equal(IssueType.Quality, issue!.Type);
        Assert.Equal("/lib/a.mkv", issue.Path);
        Assert.Equal(ItemId, issue.ItemId);
        Assert.Equal(IssueStatus.Detected, issue.Status);
        Assert.Equal("{}", issue.DetailsJson);
        Assert.Equal(0, issue.SizeSavings);
        Assert.Null(issue.Confidence);
        Assert.False(string.IsNullOrEmpty(issue.SuggestedFix));
    }

    [Fact]
    public void Build_HeavyTranscode_SetsPathTypeAndEmptyDetails()
    {
        var req = new ManualIssueRequest { Path = "/lib/b.mp4", Type = ManualIssueType.HeavyTranscode };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.Equal(IssueType.HeavyTranscode, issue!.Type);
        Assert.Equal("{}", issue.DetailsJson);
    }

    [Fact]
    public void Build_Misplaced_WritesTargetPathIntoDetails()
    {
        var req = new ManualIssueRequest
        {
            Path = "/lib/Movies/show.s01e01.mkv",
            Type = ManualIssueType.Misplaced,
            TargetPath = "/lib/TV/Show/Season 01/show.s01e01.mkv"
        };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.Equal(IssueType.Misplaced, issue!.Type);
        using var doc = JsonDocument.Parse(issue.DetailsJson);
        Assert.Equal("/lib/TV/Show/Season 01/show.s01e01.mkv",
                     doc.RootElement.GetProperty("targetPath").GetString());
    }

    [Fact]
    public void Build_FormatCompatibility_PopulatesConverterKeyAndTargetExtFromMatrix()
    {
        // .mobi is a known FormatMatrix row (ebook); converter resolves to a target extension.
        var req = new ManualIssueRequest { Path = "/lib/book.mobi", Type = ManualIssueType.FormatCompatibility };
        var (issue, problem) = MediaDashController.BuildManualIssue(req, ItemId);
        Assert.Null(problem);
        Assert.Equal(IssueType.FormatCompatibility, issue!.Type);
        using var doc = JsonDocument.Parse(issue.DetailsJson);
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("converterKey").GetString()));
        Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("targetExt").GetString()));
    }
}
