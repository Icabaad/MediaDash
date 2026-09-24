using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns every SQL statement that reads or writes the <c>issues</c> table. Extracted from
/// <see cref="MediaDashDb"/> in 05-C.2; the facade retains one-line forwarders for the same
/// methods so pre-existing callers (tests + un-migrated call sites) keep working while consumers
/// (fixers, scheduled tasks, controller endpoints) are pointed at the narrower type one by one.
/// </summary>
/// <remarks>
/// The store opens every connection through the shared <see cref="MediaDashConnectionFactory"/>
/// (05-C.1) so PRAGMAs / busy-timeout / diagnostic strings stay identical to the pre-05-C paths.
/// <c>RecordFixOutcome</c> stays on <see cref="MediaDashDb"/> because it is the sole cross-store
/// transaction — the plan (§05-C) calls that out explicitly as a "deliberate transaction entry
/// point" and warns against exposing raw connections across stores.
/// </remarks>
public sealed class IssueStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="IssueStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every store family in 05-C uses the same instance.</param>
    public IssueStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>
    /// Replaces detected issues of a type with fresh scan results, but only for the paths that were actually scanned —
    /// a scan scoped to one library must not wipe findings from other libraries.
    /// Issues the user dismissed are preserved and not re-inserted for the same path.
    /// </summary>
    /// <param name="type">The issue type being refreshed.</param>
    /// <param name="issues">The freshly detected issues.</param>
    /// <param name="scannedPaths">All file paths covered by this scan; null means the scan covered everything.</param>
    public void ReplaceDetectedIssues(IssueType type, IReadOnlyList<Issue> issues, IReadOnlyCollection<string>? scannedPaths = null)
    {
        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();

        if (scannedPaths is null)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected";
            delete.Parameters.AddWithValue("@type", (int)type);
            delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            delete.ExecuteNonQuery();
        }
        else
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected AND path = @path";
            delete.Parameters.AddWithValue("@type", (int)type);
            delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            var pPath = delete.Parameters.Add("@path", SqliteType.Text);
            foreach (var path in scannedPaths)
            {
                pPath.Value = path;
                delete.ExecuteNonQuery();
            }
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            // Dismissed rows suppress re-detection (the user said "never show this again");
            // queued rows suppress duplicates. Fixed rows must NOT suppress — the same path can break again later.
            insert.CommandText = """
                INSERT INTO issues (type, item_id, path, details, suggested_fix, size_savings, status, detected_at_utc, confidence)
                SELECT @type, @itemId, @path, @details, @suggestedFix, @sizeSavings, @detected, @detectedAt, @confidence
                WHERE NOT EXISTS (
                    SELECT 1 FROM issues WHERE type = @type AND path = @path AND status IN (@queued, @dismissed))
                """;
            insert.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
            insert.Parameters.AddWithValue("@dismissed", (int)IssueStatus.Dismissed);
            var pType = insert.Parameters.Add("@type", SqliteType.Integer);
            var pItemId = insert.Parameters.Add("@itemId", SqliteType.Text);
            var pPath = insert.Parameters.Add("@path", SqliteType.Text);
            var pDetails = insert.Parameters.Add("@details", SqliteType.Text);
            var pSuggestedFix = insert.Parameters.Add("@suggestedFix", SqliteType.Text);
            var pSizeSavings = insert.Parameters.Add("@sizeSavings", SqliteType.Integer);
            insert.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            var pDetectedAt = insert.Parameters.Add("@detectedAt", SqliteType.Integer);
            var pConfidence = insert.Parameters.Add("@confidence", SqliteType.Real);

            foreach (var issue in issues)
            {
                pType.Value = (int)type;
                pItemId.Value = issue.ItemId.ToString("N");
                pPath.Value = issue.Path;
                pDetails.Value = issue.DetailsJson;
                pSuggestedFix.Value = issue.SuggestedFix;
                pSizeSavings.Value = issue.SizeSavings;
                pDetectedAt.Value = issue.DetectedAtUtc.Ticks;
                pConfidence.Value = issue.Confidence.HasValue ? issue.Confidence.Value : (object)DBNull.Value;
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <summary>
    /// Wipes Detected rows for every supplied issue type. Queued and Dismissed rows are left
    /// untouched — the user's approval / suppression outlives a config edit.
    /// </summary>
    /// <param name="types">The affected issue types.</param>
    /// <returns>The number of rows removed.</returns>
    public int WipeDetectedForTypes(IEnumerable<IssueType> types)
    {
        var typeList = types?.Distinct().ToList() ?? new List<IssueType>();
        if (typeList.Count == 0)
        {
            return 0;
        }

        using var connection = _connectionFactory.Open();
        using var transaction = connection.BeginTransaction();
        using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = "DELETE FROM issues WHERE type = @type AND status = @detected";
        delete.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        var pType = delete.Parameters.Add("@type", SqliteType.Integer);

        var total = 0;
        foreach (var t in typeList)
        {
            pType.Value = (int)t;
            total += delete.ExecuteNonQuery();
        }

        transaction.Commit();
        return total;
    }

    /// <summary>Gets issues, optionally filtered by type and status.</summary>
    /// <param name="type">Filter by issue type, or null for all types.</param>
    /// <param name="status">Filter by status, or null for all statuses.</param>
    /// <returns>The matching issues, newest first.</returns>
    public IReadOnlyList<Issue> GetIssues(IssueType? type = null, IssueStatus? status = null)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues"
            + " WHERE (@type IS NULL OR type = @type) AND (@status IS NULL OR status = @status) ORDER BY id DESC";
        cmd.Parameters.AddWithValue("@type", type is null ? DBNull.Value : (int)type);
        cmd.Parameters.AddWithValue("@status", status is null ? DBNull.Value : (int)status);

        var result = new List<Issue>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (IssueRowReader.TryRead(reader, "issues", out var issue))
            {
                result.Add(issue);
            }
        }

        return result;
    }

    /// <summary>
    /// Gets per-type counts and potential savings for open issues (Detected + Queued), plus the newest detection time.
    /// Queued items are already approved and waiting for the fix task, so their savings still count toward "space you could reclaim".
    /// </summary>
    /// <returns>One summary row per issue type that has open issues.</returns>
    public IReadOnlyList<IssueSummary> GetSummary()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT type, COUNT(*), SUM(size_savings), MAX(detected_at_utc) FROM issues WHERE status IN (@detected, @queued) GROUP BY type";
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);

        var result = new List<IssueSummary>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!RowHydration.TryHydrateIssueType(reader.GetInt32(0), rowId: 0, "issues (GetSummary)", out var summaryType))
            {
                continue;
            }

            result.Add(new IssueSummary
            {
                Type = summaryType,
                Count = reader.GetInt32(1),
                PotentialSavings = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                // Aggregate MAX(): a single malformed detected_at_utc row makes the SQL
                // aggregate return the malformed value here. SafeUtcFromTicks clamps so the
                // whole /Status endpoint stays readable — the individual malformed row still
                // gets flagged via GetIssues's per-row Diagnostics.Record.
                NewestDetectedUtc = RowHydration.SafeUtcFromTicks(reader.GetInt64(3), "issues (GetSummary MAX)")
            });
        }

        return result;
    }

    /// <summary>Guarded status transition. See <see cref="MediaDashDb.TryTransitionIssueStatus"/> for full contract.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <param name="fromAny">Statuses that permit this transition. Empty means "any status" is rejected.</param>
    /// <param name="to">Target status.</param>
    /// <returns>Applied / NotFound / Conflict.</returns>
    public StatusTransitionResult TryTransitionIssueStatus(long issueId, IReadOnlyCollection<IssueStatus> fromAny, IssueStatus to)
    {
        if (fromAny is null || fromAny.Count == 0)
        {
            return GetIssueStatus(issueId) is null ? StatusTransitionResult.NotFound : StatusTransitionResult.Conflict;
        }

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        var names = new List<string>(fromAny.Count);
        var i = 0;
        foreach (var s in fromAny)
        {
            var name = "@from" + i.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            cmd.Parameters.AddWithValue(name, (int)s);
            i++;
        }

#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids/statuses are bound as parameters — no user text reaches the SQL string
        cmd.CommandText = "UPDATE issues SET status = @to WHERE id = @id AND status IN (" + string.Join(",", names) + ")";
#pragma warning restore CA2100, CA3001
        cmd.Parameters.AddWithValue("@to", (int)to);
        cmd.Parameters.AddWithValue("@id", issueId);
        if (cmd.ExecuteNonQuery() > 0)
        {
            return StatusTransitionResult.Applied;
        }

        // Row wasn't updated. Distinguish "no such row" from "row exists but wrong current status"
        // so callers (controller endpoints) can return 404 vs 409 correctly.
        return GetIssueStatus(issueId) is null ? StatusTransitionResult.NotFound : StatusTransitionResult.Conflict;
    }

    /// <summary>Reads a single issue's current status.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The current status, or null if the row is gone.</returns>
    public IssueStatus? GetIssueStatus(long issueId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT status FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", issueId);
        var raw = cmd.ExecuteScalar();
        if (raw is null || raw is DBNull)
        {
            return null;
        }

        var value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
        return RowHydration.TryHydrateIssueStatus(value, issueId, "issues", out var status) ? status : null;
    }

    /// <summary>Guarded bulk status update. See <see cref="MediaDashDb.BulkUpdateOpenIssueStatus"/> for full contract.</summary>
    /// <param name="ids">Issue ids to update.</param>
    /// <param name="target">Target status.</param>
    /// <returns>IDs of rows actually transitioned.</returns>
    public IReadOnlyList<long> BulkUpdateOpenIssueStatus(IReadOnlyList<long> ids, IssueStatus target)
    {
        if (ids.Count == 0)
        {
            return Array.Empty<long>();
        }

        // SQLite's default parameter limit is 999; a fresh install with 5000+ duplicates otherwise
        // throws SqliteException here. Chunk into 500-id batches — each batch runs its own UPDATE.
        const int chunkSize = 500;
        // Source-status filter depends on the target. For a Queued target (Approve action) we only
        // touch Detected rows so that RETURNING id gives us the exact just-transitioned set —
        // callers use that list to run the blocking-warning consent rollback and mustn't confuse
        // "just approved by this bulk op" with "already queued from an earlier manual approve".
        // Approving an already-Queued row was a semantic no-op anyway (SET status = Queued WHERE
        // status = Queued), so filtering it out costs nothing. For Dismissed target both Detected
        // and Queued rows still get dismissed — dismissing an already-queued row is a legitimate
        // "cancel this fix" action.
        var sourceFilter = target == IssueStatus.Queued
            ? "status = @detected"
            : "status IN (@detected, @queued)";
        using var connection = _connectionFactory.Open();
        var transitioned = new List<long>(ids.Count);
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            using var cmd = connection.CreateCommand();
            var placeholders = string.Join(",", Enumerable.Range(0, count).Select(i => "@id" + i.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids/statuses are bound as parameters — no user text reaches the SQL string
            cmd.CommandText = "UPDATE issues SET status = @target WHERE id IN (" + placeholders + ") AND " + sourceFilter + " RETURNING id";
#pragma warning restore CA2100, CA3001
            cmd.Parameters.AddWithValue("@target", (int)target);
            cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
            for (var i = 0; i < count; i++)
            {
                cmd.Parameters.AddWithValue("@id" + i.ToString(CultureInfo.InvariantCulture), ids[offset + i]);
            }

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                transitioned.Add(reader.GetInt64(0));
            }
        }

        return transitioned;
    }

    /// <summary>Bulk un-ignore: moves Dismissed rows back to Detected.</summary>
    /// <param name="ids">Issue ids to revert.</param>
    /// <returns>Number of rows actually updated.</returns>
    public int BulkRevertDismissedIssues(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
        {
            return 0;
        }

        const int chunkSize = 500;
        using var connection = _connectionFactory.Open();
        var total = 0;
        for (var offset = 0; offset < ids.Count; offset += chunkSize)
        {
            var count = Math.Min(chunkSize, ids.Count - offset);
            using var cmd = connection.CreateCommand();
            var placeholders = string.Join(",", Enumerable.Range(0, count).Select(i => "@id" + i.ToString(CultureInfo.InvariantCulture)));
#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids are bound as parameters
            cmd.CommandText = "UPDATE issues SET status = @detected WHERE id IN (" + placeholders + ") AND status = @dismissed";
#pragma warning restore CA2100, CA3001
            cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
            cmd.Parameters.AddWithValue("@dismissed", (int)IssueStatus.Dismissed);
            for (var i = 0; i < count; i++)
            {
                cmd.Parameters.AddWithValue("@id" + i.ToString(CultureInfo.InvariantCulture), ids[offset + i]);
            }

            total += cmd.ExecuteNonQuery();
        }

        return total;
    }

    /// <summary>Re-points issue rows after a move-style fixer runs. See <see cref="MediaDashDb.RelocateIssuePaths"/>.</summary>
    /// <param name="oldPath">The pre-move source path (file or directory).</param>
    /// <param name="newPath">The post-move target path.</param>
    /// <returns>The number of issue rows updated.</returns>
    public int RelocateIssuePaths(string oldPath, string newPath)
    {
        if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
        {
            return 0;
        }

        // Callers pass paths from different sources — some already TrimEndingDirectorySeparator,
        // some don't. A trailing separator collapses the boundary check (@oldSlash duplicates the
        // separator) and the prefix match silently misses. Normalize here so the callers don't have to.
        oldPath = Path.TrimEndingDirectorySeparator(oldPath);
        newPath = Path.TrimEndingDirectorySeparator(newPath);

        if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
        {
            return 0;
        }

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        // Exact match (file move) → rewrite in place; the SUBSTR tail is empty so @new stays @new.
        // Prefix match with '/' or '\' boundary (folder move) → rewrite with the folder prefix swapped.
        // Uses SUBSTR equality rather than LIKE to avoid % / _ being treated as wildcards on paths.
        //
        // Bug-pattern audit round 4 P-06 (2026-09-19): the unconditional COLLATE NOCASE below
        // came from a legitimate 1.0.0-era Windows fix (stored paths can differ in case from
        // the fixer's post-move path because scanner sources vary in case-folding), but it
        // silently merged case-only sibling directories on case-sensitive hosts. On a Linux
        // ext4/xfs/btrfs library where `/media/Film` and `/media/film` are legitimately distinct
        // directories, relocating just `/media/Film` rewrote BOTH — issues in `/media/film` now
        // pointed at the wrong destination and subsequent queued fixes silently targeted the
        // wrong media. Gate NOCASE behind Windows; on other hosts fall back to case-sensitive
        // matching. macOS default APFS is case-insensitive but can be case-sensitive on a
        // per-volume basis — erring case-sensitive there is safer: a missed rewrite becomes a
        // "no longer exists" that a fresh scan corrects, while an over-match points at the
        // wrong file (much harder to undo).
        var nocase = OperatingSystem.IsWindows() ? " COLLATE NOCASE" : string.Empty;
        cmd.CommandText = @"
            UPDATE issues
               SET path = @new || SUBSTR(path, LENGTH(@old) + 1)
             WHERE path = @old" + nocase + @"
                OR SUBSTR(path, 1, LENGTH(@old) + 1) = @oldSlash" + nocase + @"
                OR SUBSTR(path, 1, LENGTH(@old) + 1) = @oldBackslash" + nocase;
        cmd.Parameters.AddWithValue("@old", oldPath);
        cmd.Parameters.AddWithValue("@new", newPath);
        cmd.Parameters.AddWithValue("@oldSlash", oldPath + "/");
        cmd.Parameters.AddWithValue("@oldBackslash", oldPath + "\\");
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Auto-queue detected issues, respecting the restored_paths veto and confidence gate.</summary>
    /// <param name="type">The issue type.</param>
    /// <param name="minConfidence">Optional minimum <c>Issue.Confidence</c> threshold. Only pass a non-null value for confidence-gated types (currently Duplicate).</param>
    /// <returns>The IDs of the rows this call moved from Detected to Queued.</returns>
    public IReadOnlyList<long> QueueDetectedIssues(IssueType type, double? minConfidence = null)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        // The NOT EXISTS gate against restored_paths implements the "if a user restored a file, no
        // scanner may auto-fix it again" invariant. Manual approval via /Issues/{id}/Approve still
        // goes through the guarded TryTransitionIssueStatus helper and doesn't touch this method.
        // RETURNING id lets us hand the exact newly-queued set to the consent rollback pass — a
        // second read-back would race against the fixer picking rows up.
        var baseSql = """
            UPDATE issues SET status = @queued
            WHERE type = @type AND status = @detected
              AND NOT EXISTS (
                  SELECT 1 FROM restored_paths rp
                  WHERE rp.path = issues.path AND (rp.type = issues.type OR rp.type = @any)
              )
            """;

        if (minConfidence is double gate)
        {
            cmd.CommandText = baseSql + " AND (confidence IS NULL OR confidence >= @gate) RETURNING id";
            cmd.Parameters.AddWithValue("@gate", gate);
        }
        else
        {
            cmd.CommandText = baseSql + " RETURNING id";
        }

        cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
        cmd.Parameters.AddWithValue("@type", (int)type);
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@any", MediaDashSchema.RestoredPathAnyType);

        var queuedIds = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            queuedIds.Add(reader.GetInt64(0));
        }

        return queuedIds;
    }

    /// <summary>Reverts auto-queued issues whose DetailsJson carries a blocking warning back to Detected.</summary>
    /// <param name="justAutoQueuedIds">Exact set of IDs the current auto-queue pass just moved from Detected to Queued.</param>
    /// <returns>The number of issues reverted from Queued to Detected.</returns>
    public int RollbackAutoQueuedBlockingWarnings(IReadOnlyCollection<long> justAutoQueuedIds)
    {
        if (justAutoQueuedIds is null || justAutoQueuedIds.Count == 0)
        {
            return 0;
        }

        // Filter down to rows that (a) are in the just-queued set, (b) are still Queued (the fixer
        // may have already touched some), and (c) carry a blocking warning in DetailsJson.
        var justQueuedSet = new HashSet<long>(justAutoQueuedIds);
        var toRevert = GetIssues(status: IssueStatus.Queued)
            .Where(i => justQueuedSet.Contains(i.Id) && i.HasBlockingWarnings)
            .Select(i => i.Id)
            .ToList();

        if (toRevert.Count == 0)
        {
            return 0;
        }

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        // Parameterize each id — SQLite has no array binding, so we build @id0,@id1,... .
        // The list is typically 0-few items (bitmap-sub track removals are the main source), so
        // one parameterised UPDATE is cheaper than a per-row round-trip. The AND status = @queued
        // guard protects against a manual dismiss/revert landing between our read and this write.
        var placeholders = new List<string>(toRevert.Count);
        for (int i = 0; i < toRevert.Count; i++)
        {
            var name = "@id" + i.ToString(CultureInfo.InvariantCulture);
            placeholders.Add(name);
            cmd.Parameters.AddWithValue(name, toRevert[i]);
        }

#pragma warning disable CA2100, CA3001 // placeholder list is machine-composed from an int range, ids are bound as parameters
        cmd.CommandText = "UPDATE issues SET status = @detected WHERE status = @queued AND id IN ("
            + string.Join(",", placeholders) + ")";
#pragma warning restore CA2100, CA3001
        cmd.Parameters.AddWithValue("@detected", (int)IssueStatus.Detected);
        cmd.Parameters.AddWithValue("@queued", (int)IssueStatus.Queued);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Gets a single issue by id.</summary>
    /// <param name="issueId">The issue id.</param>
    /// <returns>The issue, or null when the row is gone or malformed.</returns>
    public Issue? GetIssue(long issueId)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT " + IssueRowReader.ProjectionColumns + " FROM issues WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", issueId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        // F-014 (single-row variant): tolerate malformed item_id / enum / ticks — return null
        // rather than throwing all the way up. Every caller of GetIssue already handles the
        // "no such row" null return, so the malformed case reuses that path via IssueRowReader.
        return IssueRowReader.TryRead(reader, "issues", out var issue) ? issue : null;
    }
}
