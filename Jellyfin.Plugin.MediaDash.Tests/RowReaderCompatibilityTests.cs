using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

// Package 05-A regression. Locks the projection order and malformed-row tolerance of
// IssueRowReader / HistoryRowReader as the sole hydration path for the issues + history tables.
// The plan's contract-line is "a valid row after a malformed row must still appear" and "preserve
// the existing distinction between skipping a malformed row and substituting a bounded invalid
// timestamp" — both invariants are asserted through the public reader helpers AND through the
// facade's GetIssues / GetHistory (per plan: "Test public read/query paths, not only the reader
// helper in isolation").
[Collection("MediaDashDb")]
public sealed class RowReaderCompatibilityTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-rowreader-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public RowReaderCompatibilityTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    // ─── IssueRowReader ─────────────────────────────────────────────────

    [Fact]
    public void IssueRowReader_ValidRowAfterBadGuid_IsReturnedByGetIssues()
    {
        var validItemId = Guid.NewGuid();
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: "not-a-guid", status: (int)IssueStatus.Detected, path: "/lib/bad-guid.mkv");
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: validItemId.ToString("N"), status: (int)IssueStatus.Detected, path: "/lib/good.mkv");

        var issues = _db.GetIssues();

        Assert.DoesNotContain(issues, i => i.Path == "/lib/bad-guid.mkv");
        Assert.Contains(issues, i => i.Path == "/lib/good.mkv" && i.ItemId == validItemId);
    }

    [Fact]
    public void IssueRowReader_ValidRowAfterBadEnum_IsReturnedByGetIssues()
    {
        InsertRawIssue(type: 9999, itemId: Guid.NewGuid().ToString("N"), status: (int)IssueStatus.Detected, path: "/lib/bad-type.mkv");
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: Guid.NewGuid().ToString("N"), status: (int)IssueStatus.Detected, path: "/lib/good.mkv");

        var issues = _db.GetIssues();

        Assert.DoesNotContain(issues, i => i.Path == "/lib/bad-type.mkv");
        Assert.Contains(issues, i => i.Path == "/lib/good.mkv");
    }

    [Fact]
    public void IssueRowReader_BadTicks_AreClampedNotDropped()
    {
        // Preserve the "skip vs clamp" distinction: malformed enum/GUID drops the row, malformed
        // ticks keeps the row with a bounded timestamp. GetIssues returning the "overflow" path
        // proves the clamp branch fired (a drop would have removed it entirely).
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: Guid.NewGuid().ToString("N"), status: (int)IssueStatus.Detected, path: "/lib/overflow.mkv", ticks: long.MaxValue);

        var issues = _db.GetIssues();
        var overflow = Assert.Single(issues, i => i.Path == "/lib/overflow.mkv");
        Assert.Equal(DateTimeKind.Utc, overflow.DetectedAtUtc.Kind);
        Assert.Equal(DateTime.MaxValue.Ticks, overflow.DetectedAtUtc.Ticks);
    }

    [Fact]
    public void IssueRowReader_ProjectionColumns_MatchGetIssuesReaderPositions()
    {
        // Contract lock: the reader's positional GetXxx(N) accesses assume the SELECT projected
        // via IssueRowReader.ProjectionColumns. This test drives that projection directly through
        // a raw connection so a silent reorder of ProjectionColumns would blow up here instead of
        // silently misassigning fields in production.
        var itemId = Guid.NewGuid();
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: itemId.ToString("N"), status: (int)IssueStatus.Queued, path: "/lib/projection.mkv", ticks: 12345L, details: "{\"k\":1}", suggestedFix: "recycle", sizeSavings: 42L, confidence: 0.75);

        using var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", "/lib/projection.mkv");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(IssueRowReader.TryRead(reader, "issues", out var issue));
        Assert.Equal(IssueType.Duplicate, issue.Type);
        Assert.Equal(itemId, issue.ItemId);
        Assert.Equal("/lib/projection.mkv", issue.Path);
        Assert.Equal("{\"k\":1}", issue.DetailsJson);
        Assert.Equal("recycle", issue.SuggestedFix);
        Assert.Equal(42L, issue.SizeSavings);
        Assert.Equal(IssueStatus.Queued, issue.Status);
        Assert.Equal(12345L, issue.DetectedAtUtc.Ticks);
        Assert.Equal(0.75, issue.Confidence);
    }

    [Fact]
    public void IssueRowReader_TryRead_ReturnsFalseForBadItemId()
    {
        InsertRawIssue(type: (int)IssueType.Duplicate, itemId: "not-a-guid", status: (int)IssueStatus.Detected, path: "/lib/bad.mkv");

        using var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", "/lib/bad.mkv");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(IssueRowReader.TryRead(reader, "issues", out _));
    }

    // ─── HistoryRowReader ────────────────────────────────────────────────

    [Fact]
    public void HistoryRowReader_ValidRowAfterBadEnum_IsReturnedByGetHistory()
    {
        InsertRawHistory(type: 9999, path: "/lib/bad-type.mkv", ticks: DateTime.UtcNow.Ticks);
        InsertRawHistory(type: (int)IssueType.Duplicate, path: "/lib/good.mkv", ticks: DateTime.UtcNow.Ticks);

        var history = _db.GetHistory();

        Assert.DoesNotContain(history, h => h.Path == "/lib/bad-type.mkv");
        Assert.Contains(history, h => h.Path == "/lib/good.mkv");
    }

    [Fact]
    public void HistoryRowReader_BadTicks_AreClampedNotDropped()
    {
        InsertRawHistory(type: (int)IssueType.Duplicate, path: "/lib/overflow.mkv", ticks: long.MaxValue);

        var history = _db.GetHistory();
        var overflow = Assert.Single(history, h => h.Path == "/lib/overflow.mkv");
        Assert.Equal(DateTimeKind.Utc, overflow.FixedAtUtc.Kind);
        Assert.Equal(DateTime.MaxValue.Ticks, overflow.FixedAtUtc.Ticks);
    }

    [Fact]
    public void HistoryRowReader_ProjectionColumns_MatchGetHistoryReaderPositions()
    {
        InsertRawHistory(
            type: (int)IssueType.OrphanedDebris,
            path: "/lib/history.mkv",
            ticks: 98765L,
            action: "recycled",
            bytesFreed: 100L,
            recyclePath: "/bin/history.mkv",
            dryRun: 1,
            restored: 1,
            success: 0,
            acknowledged: 1);

        using var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns + " FROM history WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", "/lib/history.mkv");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.True(HistoryRowReader.TryRead(reader, "history", out var entry));
        Assert.Equal(IssueType.OrphanedDebris, entry.Type);
        Assert.Equal("/lib/history.mkv", entry.Path);
        Assert.Equal("recycled", entry.Action);
        Assert.Equal(100L, entry.BytesFreed);
        Assert.Equal("/bin/history.mkv", entry.RecyclePath);
        Assert.Equal(98765L, entry.FixedAtUtc.Ticks);
        Assert.True(entry.WasDryRun);
        Assert.True(entry.Restored);
        Assert.False(entry.Success);
        Assert.True(entry.Acknowledged);
    }

    [Fact]
    public void HistoryRowReader_TryRead_ReturnsFalseForBadEnum()
    {
        InsertRawHistory(type: 9999, path: "/lib/bad.mkv", ticks: DateTime.UtcNow.Ticks);

        using var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns + " FROM history WHERE path = @p";
        cmd.Parameters.AddWithValue("@p", "/lib/bad.mkv");
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.False(HistoryRowReader.TryRead(reader, "history", out _));
    }

    // ─── RowHydration ────────────────────────────────────────────────────

    [Fact]
    public void RowHydration_SafeUtcFromTicks_ClampsAndReturnsUtc()
    {
        var overflow = RowHydration.SafeUtcFromTicks(long.MaxValue, "test");
        Assert.Equal(DateTimeKind.Utc, overflow.Kind);
        Assert.Equal(DateTime.MaxValue.Ticks, overflow.Ticks);

        var underflow = RowHydration.SafeUtcFromTicks(-1, "test");
        Assert.Equal(DateTimeKind.Utc, underflow.Kind);
        Assert.Equal(DateTime.MinValue.Ticks, underflow.Ticks);

        var ok = RowHydration.SafeUtcFromTicks(12345L, "test");
        Assert.Equal(DateTimeKind.Utc, ok.Kind);
        Assert.Equal(12345L, ok.Ticks);
    }

    [Fact]
    public void RowHydration_TryHydrateIssueType_RejectsUndefinedInt()
    {
        Assert.False(RowHydration.TryHydrateIssueType(9999, rowId: 0, "test", out _));
        Assert.True(RowHydration.TryHydrateIssueType((int)IssueType.Duplicate, rowId: 0, "test", out var t));
        Assert.Equal(IssueType.Duplicate, t);
    }

    [Fact]
    public void RowHydration_TryHydrateIssueStatus_RejectsUndefinedInt()
    {
        Assert.False(RowHydration.TryHydrateIssueStatus(42, rowId: 0, "test", out _));
        Assert.True(RowHydration.TryHydrateIssueStatus((int)IssueStatus.Queued, rowId: 0, "test", out var s));
        Assert.Equal(IssueStatus.Queued, s);
    }

    // ─── helpers ─────────────────────────────────────────────────────────

    private void InsertRawIssue(int type, string itemId, int status, string path, long ticks = 0, string details = "{}", string suggestedFix = "", long sizeSavings = 0, double? confidence = null)
    {
        using var conn = new SqliteConnection("Data Source=" + _dbPath + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence) VALUES (@type, @itemId, @path, @details, @suggestedFix, @sizeSavings, @status, @ticks, @confidence)";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@itemId", itemId);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@details", details);
        cmd.Parameters.AddWithValue("@suggestedFix", suggestedFix);
        cmd.Parameters.AddWithValue("@sizeSavings", sizeSavings);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@ticks", ticks == 0 ? DateTime.UtcNow.Ticks : ticks);
        cmd.Parameters.AddWithValue("@confidence", confidence.HasValue ? confidence.Value : (object)DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void InsertRawHistory(int type, string path, long ticks, string action = "seeded", long bytesFreed = 0, string? recyclePath = null, int dryRun = 0, int restored = 0, int success = 1, int acknowledged = 0)
    {
        using var conn = new SqliteConnection("Data Source=" + _dbPath + ";Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success, acknowledged) VALUES (0, @type, @path, @action, @bytesFreed, @recyclePath, @ticks, @dryRun, @restored, @success, @acknowledged)";
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@bytesFreed", bytesFreed);
        cmd.Parameters.AddWithValue("@recyclePath", (object?)recyclePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ticks", ticks);
        cmd.Parameters.AddWithValue("@dryRun", dryRun);
        cmd.Parameters.AddWithValue("@restored", restored);
        cmd.Parameters.AddWithValue("@success", success);
        cmd.Parameters.AddWithValue("@acknowledged", acknowledged);
        cmd.ExecuteNonQuery();
    }
}
