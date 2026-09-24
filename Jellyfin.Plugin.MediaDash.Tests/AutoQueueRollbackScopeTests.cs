using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// MD-003 regression: <see cref="MediaDashDb.RollbackAutoQueuedBlockingWarnings"/> must only revert
/// rows the caller identifies as newly auto-queued. Before the fix, the rollback walked ALL Queued
/// rows and reverted any that carried a blocking warning — silently discarding manual approvals
/// the user had made through the Issues tab and preventing consented fixes from ever running.
/// </summary>
[Collection("MediaDashDb")]
public sealed class AutoQueueRollbackScopeTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-rollback-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    private const string BlockingDetails = "{\"warnings\":[{\"code\":\"bitmap-subs-dropped\",\"blocking\":true,\"message\":\"drops 1 sub\"}]}";
    private static readonly IssueStatus[] FromDetected = [IssueStatus.Detected];

    public AutoQueueRollbackScopeTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void RevertsOnlyAutoQueuedBlockingRow()
    {
        // manualId: user reviewed the blocking warning and clicked Approve — should be left alone.
        // autoId: auto-queue picked it up because the type is Automatic — should be reverted.
        var ids = InsertIssues(
            ("/lib/manual.mkv", BlockingDetails),
            ("/lib/auto.mkv", BlockingDetails));
        var manualId = ids["/lib/manual.mkv"];
        var autoId = ids["/lib/auto.mkv"];

        _db.TryTransitionIssueStatus(manualId, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(autoId, FromDetected, IssueStatus.Queued);

        // Only autoId is in the "just auto-queued" set. Rollback should touch nothing else.
        var reverted = _db.RollbackAutoQueuedBlockingWarnings(new List<long> { autoId });

        Assert.Equal(1, reverted);
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(manualId));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(autoId));
    }

    [Fact]
    public void EmptyIdListIsNoOp()
    {
        // Auto-queue produced nothing this pass — the rollback must not fall back to "all queued
        // blocking rows", which is exactly the MD-003 regression.
        var ids = InsertIssues(("/lib/manual.mkv", BlockingDetails));
        var manualId = ids["/lib/manual.mkv"];
        _db.TryTransitionIssueStatus(manualId, FromDetected, IssueStatus.Queued);

        var reverted = _db.RollbackAutoQueuedBlockingWarnings([]);

        Assert.Equal(0, reverted);
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(manualId));
    }

    [Fact]
    public void IgnoresIdsThatAreNoLongerQueued()
    {
        // Row was auto-queued, then the fixer completed it. When rollback runs, its status is
        // Fixed. The `status = @queued` guard in the UPDATE means the row is left alone — we don't
        // want to resurrect a Fixed row.
        var ids = InsertIssues(("/lib/fixed.mkv", BlockingDetails));
        var autoId = ids["/lib/fixed.mkv"];
        _db.TryTransitionIssueStatus(autoId, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(autoId, [IssueStatus.Queued], IssueStatus.Fixed);

        var reverted = _db.RollbackAutoQueuedBlockingWarnings(new List<long> { autoId });

        Assert.Equal(0, reverted);
        Assert.Equal(IssueStatus.Fixed, _db.GetIssueStatus(autoId));
    }

    [Fact]
    public void SkipsAutoQueuedRowsWithoutBlockingWarnings()
    {
        // A non-blocking auto-queued row must NOT be reverted; those are what Automatic mode is
        // for. Only blocking-warning rows require the consent gate.
        var ids = InsertIssues(
            ("/lib/normal.mkv", string.Empty),
            ("/lib/blocking.mkv", BlockingDetails));
        var normalId = ids["/lib/normal.mkv"];
        var blockingId = ids["/lib/blocking.mkv"];

        _db.TryTransitionIssueStatus(normalId, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(blockingId, FromDetected, IssueStatus.Queued);

        var reverted = _db.RollbackAutoQueuedBlockingWarnings(new List<long> { normalId, blockingId });

        Assert.Equal(1, reverted);
        Assert.Equal(IssueStatus.Queued, _db.GetIssueStatus(normalId));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(blockingId));
    }

    private Dictionary<string, long> InsertIssues(params (string path, string detailsJson)[] rows)
    {
        var issues = new List<Issue>(rows.Length);
        foreach (var (path, detailsJson) in rows)
        {
            issues.Add(new Issue
            {
                Type = IssueType.AudioLanguage,
                ItemId = Guid.NewGuid(),
                Path = path,
                DetailsJson = detailsJson,
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            });
        }

        _db.ReplaceDetectedIssues(IssueType.AudioLanguage, issues);

        var pathToId = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in _db.GetIssues(IssueType.AudioLanguage, IssueStatus.Detected))
        {
            pathToId[row.Path] = row.Id;
        }

        foreach (var (path, _) in rows)
        {
            Assert.True(pathToId.ContainsKey(path), "Inserted issue not found: " + path);
        }

        return pathToId;
    }
}
