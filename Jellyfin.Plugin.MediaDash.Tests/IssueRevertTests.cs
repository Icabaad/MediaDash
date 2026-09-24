using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// State transitions for the Issues-tab Undo button and the wider status-machine contract enforced
/// by <see cref="MediaDashDb.TryTransitionIssueStatus"/> (CLAUDE.md safety invariant 7). Revert
/// must be legal from Queued and Dismissed, and never from Fixed; Approve is only legal from
/// Detected; Dismiss is legal from Detected or Queued. Every transition happens inside a single
/// UPDATE so a stale request can't overwrite a Fixed row or race with a concurrent decision.
/// </summary>
[Collection("MediaDashDb")]
public sealed class IssueRevertTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-revert-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    private static readonly IssueStatus[] FromDetected = [IssueStatus.Detected];
    private static readonly IssueStatus[] FromQueuedOrDismissed = [IssueStatus.Queued, IssueStatus.Dismissed];
    private static readonly IssueStatus[] FromDetectedOrQueued = [IssueStatus.Detected, IssueStatus.Queued];
    private static readonly IssueStatus[] FromQueued = [IssueStatus.Queued];

    public IssueRevertTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void QueuedCanRevertToDetected()
    {
        var id = InsertIssue();
        Assert.Equal(StatusTransitionResult.Applied, _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued));

        Assert.Equal(StatusTransitionResult.Applied, _db.TryTransitionIssueStatus(id, FromQueuedOrDismissed, IssueStatus.Detected));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(id));
    }

    [Fact]
    public void DismissedCanRevertToDetected()
    {
        var id = InsertIssue();
        Assert.Equal(StatusTransitionResult.Applied, _db.TryTransitionIssueStatus(id, FromDetectedOrQueued, IssueStatus.Dismissed));

        Assert.Equal(StatusTransitionResult.Applied, _db.TryTransitionIssueStatus(id, FromQueuedOrDismissed, IssueStatus.Detected));
        Assert.Equal(IssueStatus.Detected, _db.GetIssueStatus(id));
    }

    [Fact]
    public void RevertingRemovesQueuedFromTheFixQueueSnapshot()
    {
        // The FixTask pulls Queued issues via GetIssues(status: Queued). Reverting must remove the
        // issue from that pull, otherwise the fix task drains a queued-but-cancelled item next
        // interval.
        var id = InsertIssue();
        _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(id, FromQueuedOrDismissed, IssueStatus.Detected);

        var queuedNow = _db.GetIssues(status: IssueStatus.Queued);
        Assert.DoesNotContain(queuedNow, i => i.Id == id);
    }

    [Fact]
    public void UnknownIssueIdReturnsNotFound()
    {
        Assert.Null(_db.GetIssueStatus(9999));
        Assert.Equal(StatusTransitionResult.NotFound, _db.TryTransitionIssueStatus(9999, FromQueuedOrDismissed, IssueStatus.Detected));
    }

    // MD-002 regression: a Fixed row must reject every Approve/Dismiss/Revert attempt. Before the
    // atomic transition helper, UpdateIssueStatus would happily flip a Fixed row back to Queued
    // (letting a stale Approve resurrect a completed fix) or to Dismissed (rewriting the audit
    // trail after the file had already been recycled).
    [Fact]
    public void FixedRowRejectsApprove()
    {
        var id = InsertIssue();
        _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(id, FromQueued, IssueStatus.Fixed);

        Assert.Equal(StatusTransitionResult.Conflict, _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued));
        Assert.Equal(IssueStatus.Fixed, _db.GetIssueStatus(id));
    }

    [Fact]
    public void FixedRowRejectsDismiss()
    {
        var id = InsertIssue();
        _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(id, FromQueued, IssueStatus.Fixed);

        Assert.Equal(StatusTransitionResult.Conflict, _db.TryTransitionIssueStatus(id, FromDetectedOrQueued, IssueStatus.Dismissed));
        Assert.Equal(IssueStatus.Fixed, _db.GetIssueStatus(id));
    }

    [Fact]
    public void FixedRowRejectsRevert()
    {
        var id = InsertIssue();
        _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(id, FromQueued, IssueStatus.Fixed);

        Assert.Equal(StatusTransitionResult.Conflict, _db.TryTransitionIssueStatus(id, FromQueuedOrDismissed, IssueStatus.Detected));
        Assert.Equal(IssueStatus.Fixed, _db.GetIssueStatus(id));
    }

    [Fact]
    public void FixerCompletionRejectsRowDismissedMidRun()
    {
        // Simulates the race the review flagged: fix loop pulled a Queued row, user tapped Dismiss
        // in the UI mid-run, then the fixer tries to mark it Fixed. Old UpdateIssueStatus would
        // silently overwrite the user's decision; MarkFixed's [Queued] guard must refuse instead.
        var id = InsertIssue();
        _db.TryTransitionIssueStatus(id, FromDetected, IssueStatus.Queued);
        _db.TryTransitionIssueStatus(id, FromDetectedOrQueued, IssueStatus.Dismissed);

        Assert.Equal(StatusTransitionResult.Conflict, _db.TryTransitionIssueStatus(id, FromQueued, IssueStatus.Fixed));
        Assert.Equal(IssueStatus.Dismissed, _db.GetIssueStatus(id));
    }

    private long InsertIssue()
    {
        _db.ReplaceDetectedIssues(IssueType.OrphanedDebris, [new Issue
        {
            Type = IssueType.OrphanedDebris,
            ItemId = Guid.NewGuid(),
            Path = "/lib/orphan.srt",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow
        }]);

        var rows = _db.GetIssues(IssueType.OrphanedDebris, IssueStatus.Detected);
        Assert.Single(rows);
        return rows[0].Id;
    }
}
