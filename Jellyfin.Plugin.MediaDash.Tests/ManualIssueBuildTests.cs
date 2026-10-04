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

    [Fact]
    public void Build_RejectsEmptyPath()
    {
        var (issue, problem) = MediaDashController.BuildManualIssue(
            new ManualIssueRequest { Path = "", Type = ManualIssueType.Quality }, ItemId);
        Assert.Null(issue);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Build_FormatCompatibility_RejectsExtensionWithNoConverter()
    {
        // .mkv has no FormatMatrix row that produces a conversion target — it's a video container
        // that Jellyfin already renders. If this ever changes, pick another unknown ext.
        var (issue, problem) = MediaDashController.BuildManualIssue(
            new ManualIssueRequest { Path = "/lib/a.mkv", Type = ManualIssueType.FormatCompatibility }, ItemId);
        Assert.Null(issue);
        Assert.Contains(".mkv", problem!, StringComparison.OrdinalIgnoreCase);
    }
}
