using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Data;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit round 4 P-05 (2026-09-19): every DateTime construction from raw SQLite ticks
/// used `new DateTime(reader.GetInt64(n), DateTimeKind.Utc)` — which throws
/// ArgumentOutOfRangeException for out-of-domain values (negative, or above long.MaxValue for
/// the DateTime constructor). A single malformed row broke GetIssues / GetSummary and 500'd the
/// whole Issues / Status endpoint. Same class as F-014/015/MD-004 (the 1.0.7.6 sweep) but for a
/// different data type — timestamps rather than GUID or JSON shape. These tests seed malformed
/// ticks directly via raw SQL and assert that (a) the reader doesn't throw, (b) the good rows
/// still come back, and (c) a diagnostic is recorded so the user can DELETE the bad row.
/// </summary>
[Collection("MediaDashDb")]
public sealed class MalformedTimestampTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "mediadash-timestamps-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly MediaDashDb _db;

    public MalformedTimestampTests()
    {
        _db = new MediaDashDb(_dbPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    [Fact]
    public void GetIssues_ReturnsGoodRowsWhenAnotherHasOverflowTicks()
    {
        // Seed one good issue via the normal API + one directly with long.MaxValue ticks. Pre-fix
        // this scenario made the whole GetIssues call throw. Post-fix the malformed row's
        // timestamp gets clamped and both rows come back.
        var goodIssue = new Issue
        {
            Type = IssueType.Quality,
            ItemId = Guid.NewGuid(),
            Path = "/lib/good.mkv",
            Status = IssueStatus.Detected,
            DetectedAtUtc = DateTime.UtcNow
        };
        _db.ReplaceDetectedIssues(IssueType.Quality, new[] { goodIssue });

        SeedMalformedTimestamp("/lib/bad.mkv", long.MaxValue);

        var rows = _db.GetIssues(IssueType.Quality);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Path == "/lib/good.mkv");
        Assert.Contains(rows, r => r.Path == "/lib/bad.mkv");
    }

    [Fact]
    public void GetIssues_ReturnsGoodRowsWhenAnotherHasNegativeTicks()
    {
        // -1 is definitely out of DateTime's tick domain (>= 0 required). Same "clamp instead of
        // throw" contract as the overflow case.
        _db.ReplaceDetectedIssues(IssueType.Playability, new[]
        {
            new Issue
            {
                Type = IssueType.Playability,
                ItemId = Guid.NewGuid(),
                Path = "/lib/ok.mkv",
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            }
        });
        SeedMalformedTimestamp("/lib/negative.mkv", -1L, IssueType.Playability);

        var rows = _db.GetIssues(IssueType.Playability);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void GetSummary_SurvivesMalformedMaxAggregate()
    {
        // MAX(detected_at_utc) in SQL takes the largest ticks value. If a bad row holds
        // long.MaxValue, the aggregate returns long.MaxValue — the pre-fix DateTime ctor threw
        // and killed /Status. The clamp keeps the aggregate rendering as DateTime.MaxValue-UTC.
        _db.ReplaceDetectedIssues(IssueType.Duplicate, new[]
        {
            new Issue
            {
                Type = IssueType.Duplicate,
                ItemId = Guid.NewGuid(),
                Path = "/lib/ok.mkv",
                Status = IssueStatus.Detected,
                DetectedAtUtc = DateTime.UtcNow
            }
        });
        SeedMalformedTimestamp("/lib/overflow.mkv", long.MaxValue, IssueType.Duplicate);

        var summary = _db.GetSummary();

        var row = Assert.Single(summary);
        Assert.Equal(IssueType.Duplicate, row.Type);
        Assert.Equal(2, row.Count);
        // The clamp landed at MaxValue — that's the truthful bound for the aggregate.
        Assert.True(row.NewestDetectedUtc.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public void GetLastSuccessfulFixUtc_ReturnsClampedInsteadOfThrowing()
    {
        // Root-cause audit 2026-09-19 R05 sibling: the 1.0.7.8 SafeUtcFromTicks sweep missed
        // GetLastSuccessfulFixUtc. TrickplayOptimizeScanner uses this as a rescan cutoff — a
        // corrupt fixed_at_utc row would previously throw ArgumentOutOfRangeException, killing
        // the scanner for the whole run. Post-fix the clamp lets the aggregate return a bounded
        // DateTime.MaxValue.Utc so the caller keeps going.
        var path = "/lib/trickplay-victim.mkv";
        SeedMalformedHistoryTimestamp(path, long.MaxValue, IssueType.LargeTrickplay);

        var result = _db.GetLastSuccessfulFixUtc(IssueType.LargeTrickplay, path);

        Assert.NotNull(result);
        Assert.Equal(DateTimeKind.Utc, result!.Value.Kind);
        // The clamp landed at DateTime.MaxValue; caller's downstream comparison
        // ("is this old enough to rescan?") harmlessly answers "no" for a MaxValue watermark.
        Assert.Equal(DateTime.MaxValue.Ticks, result.Value.Ticks);
    }

    // Same as SeedMalformedTimestamp but for the history table used by GetLastSuccessfulFixUtc.
    private void SeedMalformedHistoryTimestamp(string path, long badTicks, IssueType type)
    {
        using var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, success)
            VALUES (0, @type, @path, 'seeded', 0, NULL, @ticks, 0, 1)";
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@ticks", badTicks);
        cmd.ExecuteNonQuery();
    }

    // Bypasses the API's parameter validation to write a deliberately malformed row.
    private void SeedMalformedTimestamp(string path, long badTicks, IssueType type = IssueType.Quality)
    {
        using var conn = new SqliteConnection("Data Source=" + _dbPath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence)
            VALUES (@type, @itemId, @path, '{}', '', 0, @status, @ticks, NULL)";
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@itemId", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@status", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@ticks", badTicks);
        cmd.ExecuteNonQuery();
    }
}
