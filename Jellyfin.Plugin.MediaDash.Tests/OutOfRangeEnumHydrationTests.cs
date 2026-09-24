using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// MD-004 regression: CLAUDE.md safety invariant 6 requires malformed DB rows (including
/// out-of-range enum values) to be logged and skipped, not silently cast to an undefined
/// enum value that flows into fixer/scanner decisions with unpredictable behavior.
/// These tests seed a row with an intentionally out-of-range <c>type</c> or <c>status</c>
/// column and assert every read surface returns remaining valid rows without throwing.
/// </summary>
[Collection("MediaDashDb")]
public sealed class OutOfRangeEnumHydrationTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-enumhydration-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public OutOfRangeEnumHydrationTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void GetIssues_SkipsRowsWithInvalidType()
    {
        var validId = InsertValidIssue(IssueType.OrphanedDebris, "/lib/valid.mkv");
        var badId = InsertRawIssue(type: 9999, status: (int)IssueStatus.Detected, path: "/lib/bad-type.mkv");

        var rows = _db.GetIssues();

        Assert.Contains(rows, r => r.Id == validId);
        Assert.DoesNotContain(rows, r => r.Id == badId);
    }

    [Fact]
    public void GetIssues_SkipsRowsWithInvalidStatus()
    {
        var validId = InsertValidIssue(IssueType.OrphanedDebris, "/lib/valid.mkv");
        var badId = InsertRawIssue(type: (int)IssueType.OrphanedDebris, status: 42, path: "/lib/bad-status.mkv");

        var rows = _db.GetIssues();

        Assert.Contains(rows, r => r.Id == validId);
        Assert.DoesNotContain(rows, r => r.Id == badId);
    }

    [Fact]
    public void GetIssue_ReturnsNullForInvalidEnumRow()
    {
        var badId = InsertRawIssue(type: 9999, status: (int)IssueStatus.Detected, path: "/lib/bad.mkv");

        Assert.Null(_db.GetIssue(badId));
    }

    [Fact]
    public void GetIssueStatus_ReturnsNullForInvalidStatusRow()
    {
        var badId = InsertRawIssue(type: (int)IssueType.OrphanedDebris, status: 42, path: "/lib/bad-status.mkv");

        Assert.Null(_db.GetIssueStatus(badId));
    }

    [Fact]
    public void GetIssues_DoesNotThrowWhenEveryRowIsMalformed()
    {
        InsertRawIssue(type: 9999, status: (int)IssueStatus.Detected, path: "/lib/bad1.mkv");
        InsertRawIssue(type: (int)IssueType.OrphanedDebris, status: 42, path: "/lib/bad2.mkv");

        var rows = _db.GetIssues();

        Assert.Empty(rows);
    }

    private long InsertValidIssue(IssueType type, string path)
    {
        _db.ReplaceDetectedIssues(type, [new Issue
        {
            Type = type,
            ItemId = Guid.NewGuid(),
            Path = path,
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow
        }]);

        foreach (var row in _db.GetIssues(type, IssueStatus.Detected))
        {
            if (row.Path == path)
            {
                return row.Id;
            }
        }

        throw new InvalidOperationException("Inserted issue not found: " + path);
    }

    private long InsertRawIssue(int type, int status, string path)
    {
        // Bypass ReplaceDetectedIssues (which would validate type via its @type parameter) by
        // writing directly with an out-of-range integer. Simulates a corrupt DB row a legacy
        // version or a hand-edit could leave behind.
        // Pooling=False keeps this raw injection from leaving a lingering pooled handle that
        // races another test's ClearAllPools/File.Delete cleanup when xunit runs in parallel.
        using var conn = new SqliteConnection("Data Source=" + _dbPath + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence)
            VALUES (@type, @itemId, @path, '', '', 0, @status, @detectedAt, NULL);
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@itemId", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@detectedAt", DateTime.UtcNow.Ticks);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }
}
