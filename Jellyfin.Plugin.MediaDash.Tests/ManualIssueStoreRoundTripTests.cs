using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Round-trips a manual-add payload through <see cref="IssueStore.ReplaceDetectedIssues"/>
/// to pin two guarantees the controller endpoint relies on:
///  1. Idempotency — pressing "Add to issues" twice for the same (type, path) leaves one row.
///  2. Suppression — a subsequent manual add while the earlier row is Queued does NOT insert
///     a duplicate; the Queued row survives untouched.
/// </summary>
[Collection("MediaDashDb")]
public sealed class ManualIssueStoreRoundTripTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-manual-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;
    private readonly IssueStore _store;

    public ManualIssueStoreRoundTripTests()
    {
        _db = new MediaDashDb(_dbPath);
        _store = new IssueStore(_db.ConnectionFactory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void AddingQualityTwice_LeavesOneDetectedRow()
    {
        var itemId = Guid.NewGuid();
        var path = "/lib/a.mkv";
        var req = new ManualIssueRequest { Path = path, Type = ManualIssueType.Quality };

        var (issue1, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { issue1! }, scannedPaths: new[] { path });

        var (issue2, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { issue2! }, scannedPaths: new[] { path });

        var rows = _store.GetIssues(type: IssueType.Quality);
        Assert.Single(rows);
        Assert.Equal(IssueStatus.Detected, rows[0].Status);
    }

    [Fact]
    public void AddingQuality_WhenAlreadyQueued_IsSuppressed()
    {
        var itemId = Guid.NewGuid();
        var path = "/lib/a.mkv";
        var req = new ManualIssueRequest { Path = path, Type = ManualIssueType.Quality };

        var (first, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { first! }, scannedPaths: new[] { path });
        var detectedId = _store.GetIssues(type: IssueType.Quality).Single().Id;
        _store.BulkUpdateOpenIssueStatus(new[] { detectedId }, IssueStatus.Queued);

        // Second manual add while Queued: should NOT insert a second row.
        var (second, _) = MediaDashController.BuildManualIssue(req, itemId);
        _store.ReplaceDetectedIssues(IssueType.Quality, new[] { second! }, scannedPaths: new[] { path });

        var rows = _store.GetIssues(type: IssueType.Quality);
        Assert.Single(rows);
        Assert.Equal(IssueStatus.Queued, rows[0].Status);
    }
}
