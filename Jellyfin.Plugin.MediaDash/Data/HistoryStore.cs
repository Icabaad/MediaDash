using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns every SQL statement that reads or writes the <c>history</c> table. Extracted from
/// <see cref="MediaDashDb"/> in 05-C.3; the facade retains one-line forwarders for the same
/// methods so pre-existing callers (tests + un-migrated call sites) keep working while consumers
/// (AnalyticsReporter, FileBrowserController, TrickplayOptimizeScanner, RestoreService, RecycleBin,
/// RedownloadDetector, MediaDashController) are pointed at the narrower type one by one.
/// </summary>
/// <remarks>
/// <c>MediaDashDb.RecordFixOutcome</c> stays on the facade — it is the ONE method that atomically
/// touches both <c>history</c> and <c>issues</c> in a single SQLite transaction, and plan §05-C
/// warns against exposing raw connections across stores. Treat <c>MediaDashDb</c> itself as the
/// deliberate cross-store transaction entry point: single-table operations live on the per-family
/// stores, cross-table transactions live on the facade.
/// </remarks>
public sealed class HistoryStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="HistoryStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every 05-C store family uses the same instance.</param>
    public HistoryStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Records a fix action in the history.</summary>
    /// <param name="entry">The history entry.</param>
    public void AddHistory(HistoryEntry entry)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO history (issue_id, type, path, action, bytes_freed, recycle_path, fixed_at_utc, dry_run, restored, success)
            VALUES (@issueId, @type, @path, @action, @bytesFreed, @recyclePath, @fixedAt, @dryRun, 0, @success)
            """;
        cmd.Parameters.AddWithValue("@issueId", entry.IssueId);
        cmd.Parameters.AddWithValue("@type", (int)entry.Type);
        cmd.Parameters.AddWithValue("@path", entry.Path);
        cmd.Parameters.AddWithValue("@action", entry.Action);
        cmd.Parameters.AddWithValue("@bytesFreed", entry.BytesFreed);
        cmd.Parameters.AddWithValue("@recyclePath", (object?)entry.RecyclePath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@fixedAt", entry.FixedAtUtc.Ticks);
        cmd.Parameters.AddWithValue("@dryRun", entry.WasDryRun ? 1 : 0);
        cmd.Parameters.AddWithValue("@success", entry.Success ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Gets fix history, newest first.</summary>
    /// <param name="limit">Maximum rows returned.</param>
    /// <returns>The history entries.</returns>
    public IReadOnlyList<HistoryEntry> GetHistory(int limit = 500)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns
            + " FROM history ORDER BY id DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var result = new List<HistoryEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (HistoryRowReader.TryRead(reader, "history", out var entry))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets every distinct recycle path recorded by non-dry-run history. These paths are authoritative
    /// evidence for adopting pre-marker batches after an upgrade.
    /// </summary>
    /// <returns>Recorded recycle paths.</returns>
    public IReadOnlyList<string> GetRecyclePaths()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT recycle_path FROM history"
            + " WHERE recycle_path IS NOT NULL AND recycle_path <> '' AND dry_run = 0";

        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    /// <summary>
    /// Marks a history row as acknowledged so the redownload-warning banner stops flagging it.
    /// No-op when the row does not exist.
    /// </summary>
    /// <param name="historyId">The history row id.</param>
    /// <returns>True when a row was updated.</returns>
    public bool AcknowledgeHistoryEntry(long historyId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE history SET acknowledged = 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>Gets a single history row by id.</summary>
    /// <param name="historyId">The row id.</param>
    /// <returns>The row, or null.</returns>
    public HistoryEntry? GetHistoryEntry(long historyId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + HistoryRowReader.ProjectionColumns
            + " FROM history WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", historyId);

        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return HistoryRowReader.TryRead(reader, "history", out var entry) ? entry : null;
    }

    /// <summary>
    /// Returns the most recent successful, non-dry-run <c>fixed_at_utc</c> for a given issue type and
    /// path, or null when there's no such row. Used by <see cref="Scanners.TrickplayOptimizeScanner"/>
    /// as a rescan cutoff so folders where the fixer intentionally left small "won't-shrink" sprites
    /// don't get re-flagged forever.
    /// </summary>
    /// <param name="type">The issue type.</param>
    /// <param name="path">The exact path recorded on the history row (trickplay folder for LargeTrickplay).</param>
    /// <returns>UTC timestamp of the newest matching row, or null when none.</returns>
    public DateTime? GetLastSuccessfulFixUtc(IssueType type, string path)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT MAX(fixed_at_utc) FROM history WHERE type = @type AND path = @path AND success = 1 AND dry_run = 0";
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@path", path);
        var raw = cmd.ExecuteScalar();
        // R05 sibling (audit 2026-09-19): the 1.0.7.8 SafeUtcFromTicks sweep missed this site.
        // MAX() over a malformed fixed_at_utc row returns the corrupt value; the pre-fix raw
        // ctor threw ArgumentOutOfRangeException and killed TrickplayOptimizeScanner's rescan
        // cutoff for the whole run.
        return raw is long ticks and > 0 ? RowHydration.SafeUtcFromTicks(ticks, "history (GetLastSuccessfulFixUtc MAX)") : null;
    }

    /// <summary>
    /// Counts failed non-dry-run history rows since the given watermark (Config.HistoryHiddenBeforeUtcTicks).
    /// Powers the History tab's badge. 0 for a fresh install or when everything has been cleared.
    /// </summary>
    /// <param name="hiddenBeforeUtcTicks">The Clear-history watermark; 0 = count everything.</param>
    /// <returns>Row count.</returns>
    public int GetFailedHistoryCount(long hiddenBeforeUtcTicks)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM history WHERE success = 0 AND dry_run = 0 AND fixed_at_utc >= @hidden";
        cmd.Parameters.AddWithValue("@hidden", hiddenBeforeUtcTicks);
        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : 0;
    }

    /// <summary>
    /// Returns the lifetime total of bytes reclaimed by successful non-dry-run fixes. Same
    /// success=1 AND dry_run=0 filter as the monthly aggregate so the number the user sees on
    /// Overview matches what actually happened to their disk. Returns 0 for a fresh install.
    /// </summary>
    /// <returns>Total bytes freed across the entire history.</returns>
    public long GetLifetimeBytesFreed()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(bytes_freed), 0) FROM history WHERE success = 1 AND dry_run = 0";
        var raw = cmd.ExecuteScalar();
        return raw is long l ? l : 0L;
    }

    /// <summary>
    /// Returns every successful non-dry-run history row as (path, bytes_freed). Used by the History
    /// tab's per-library chart so the totals reflect ALL history — not just the newest 500 rows the
    /// paginated <see cref="GetHistory(int)"/> returns.
    /// </summary>
    /// <returns>One row per successful non-dry-run fix.</returns>
    public IReadOnlyList<(string Path, long BytesFreed)> GetLifetimePathTotals()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT path, bytes_freed FROM history WHERE success = 1 AND dry_run = 0 AND bytes_freed > 0";
        var result = new List<(string, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return result;
    }

    /// <summary>Per-type breakdown of the lifetime reclaim total.</summary>
    /// <returns>One row per type that ever produced a successful non-dry-run fix.</returns>
    public IReadOnlyList<IssueSummary> GetLifetimeSummary()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), COALESCE(SUM(bytes_freed), 0)"
            + " FROM history"
            + " WHERE success = 1 AND dry_run = 0"
            + " GROUP BY type";

        var result = new List<IssueSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!RowHydration.TryHydrateIssueType(reader.GetInt32(0), rowId: 0, "history (lifetime aggregate)", out var lifetimeType))
            {
                continue;
            }

            result.Add(new IssueSummary
            {
                Type = lifetimeType,
                Count = reader.GetInt32(1),
                PotentialSavings = reader.GetInt64(2),
                NewestDetectedUtc = DateTime.MinValue
            });
        }

        return result;
    }

    /// <summary>Aggregates the history for a given month into per-type success counts + total bytes freed.</summary>
    /// <param name="monthStartUtc">First instant of the target month (UTC).</param>
    /// <param name="monthEndUtc">First instant of the following month (UTC) — exclusive upper bound.</param>
    /// <returns>A per-type count map plus the total bytes freed.</returns>
    public MonthAggregate GetMonthAggregate(DateTime monthStartUtc, DateTime monthEndUtc)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), COALESCE(SUM(bytes_freed), 0)"
            + " FROM history"
            + " WHERE success = 1 AND dry_run = 0"
            + " AND fixed_at_utc >= @start AND fixed_at_utc < @end"
            + " GROUP BY type";
        cmd.Parameters.AddWithValue("@start", monthStartUtc.Ticks);
        cmd.Parameters.AddWithValue("@end", monthEndUtc.Ticks);

        var byType = new Dictionary<IssueType, int>();
        long totalBytes = 0;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!RowHydration.TryHydrateIssueType(reader.GetInt32(0), rowId: 0, "history (month aggregate)", out var monthType))
            {
                continue;
            }

            byType[monthType] = reader.GetInt32(1);
            totalBytes += reader.GetInt64(2);
        }

        return new MonthAggregate(byType, totalBytes);
    }
}
