using System;
using System.IO;
using System.Text.Json;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests.Formats;

/// <summary>
/// Verifies that the Approve endpoint patches approvedInMixedLibrary=true into DetailsJson
/// for FormatCompatibility issues whose DetailsJson carries mixedLibrary=true,
/// so FormatCompatibilityFixer will honor the user's explicit approval.
/// </summary>
[Collection("MediaDashDb")]
public sealed class FormatCompatibilityApproveTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-approve-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public FormatCompatibilityApproveTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    // -- PatchApprovedInMixedLibrary unit tests --

    [Fact]
    public void PatchApprovedInMixedLibrary_ReturnsPatchedJson_WhenMixedLibraryTrue()
    {
        var input = JsonSerializer.Serialize(new { sourceExt = ".mobi", mixedLibrary = true });
        var result = MediaDashController.PatchApprovedInMixedLibrary(input);
        Assert.NotNull(result);
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("approvedInMixedLibrary").GetBoolean());
        // Original props preserved.
        Assert.Equal(".mobi", doc.RootElement.GetProperty("sourceExt").GetString());
    }

    [Fact]
    public void PatchApprovedInMixedLibrary_ReturnsNull_WhenMixedLibraryFalse()
    {
        var input = JsonSerializer.Serialize(new { sourceExt = ".mobi", mixedLibrary = false });
        Assert.Null(MediaDashController.PatchApprovedInMixedLibrary(input));
    }

    [Fact]
    public void PatchApprovedInMixedLibrary_ReturnsNull_WhenAlreadyApproved()
    {
        var input = JsonSerializer.Serialize(new { mixedLibrary = true, approvedInMixedLibrary = true });
        Assert.Null(MediaDashController.PatchApprovedInMixedLibrary(input));
    }

    [Fact]
    public void PatchApprovedInMixedLibrary_ReturnsNull_WhenJsonMalformed()
    {
        Assert.Null(MediaDashController.PatchApprovedInMixedLibrary("not-json"));
    }

    // -- IssueStore integration: UpdateDetails persists the patched JSON --

    [Fact]
    public void UpdateDetails_PersistsPatchedDetailsJson()
    {
        var original = JsonSerializer.Serialize(new { sourceExt = ".mobi", mixedLibrary = true });
        var issue = new Issue
        {
            Type = IssueType.FormatCompatibility,
            ItemId = Guid.NewGuid(),
            Path = "/library/file.mobi",
            DetailsJson = original,
            SuggestedFix = "Convert",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow
        };
        _db.ReplaceDetectedIssues(IssueType.FormatCompatibility, [issue]);
        var stored = _db.GetIssues(IssueType.FormatCompatibility, IssueStatus.Detected);
        Assert.Single(stored);
        var id = stored[0].Id;

        var patched = MediaDashController.PatchApprovedInMixedLibrary(stored[0].DetailsJson);
        Assert.NotNull(patched);
        _db.Issues.UpdateDetails(id, patched);

        var after = _db.Issues.GetIssue(id);
        Assert.NotNull(after);
        using var doc = JsonDocument.Parse(after.DetailsJson);
        Assert.True(doc.RootElement.GetProperty("approvedInMixedLibrary").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("mixedLibrary").GetBoolean());
    }
}
