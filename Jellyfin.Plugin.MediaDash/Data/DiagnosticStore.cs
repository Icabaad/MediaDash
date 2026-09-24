using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns every SQL statement that reads or writes the <c>diagnostics</c> table (the Errors-tab
/// backing store). Extracted from <see cref="MediaDashDb"/> in 05-C.5; the facade retains
/// one-line forwarders so pre-existing callers (tests + <see cref="Api.DiagnosticService"/>'s
/// former MediaDashDb-typed Attach signature is now the store-typed variant) keep working.
/// </summary>
/// <remarks>
/// No cross-store atomicity concerns. <c>SaveDiagnostic</c> runs INSERT + trim in one connection
/// but not one transaction — the trim is bounded by <c>maxRows</c> and drops the oldest, so a
/// concurrent write racing between INSERT and trim just delays the ring-buffer collapse by one
/// row. Behavior preserved verbatim from the pre-05-C.5 MediaDashDb implementation.
/// </remarks>
public sealed class DiagnosticStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory; every 05-C store family uses the same instance.</param>
    public DiagnosticStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Upserts a diagnostic entry (dedup on source + messageHash) and trims to newest <paramref name="maxRows"/>.</summary>
    /// <param name="source">Diagnostic source tag.</param>
    /// <param name="messageHash">Stable 32-bit hash of the pre-truncation message.</param>
    /// <param name="message">The (possibly truncated) message text.</param>
    /// <param name="atUtc">Timestamp of the first occurrence in the current group.</param>
    /// <param name="lastAtUtc">Timestamp of the current occurrence.</param>
    /// <param name="maxRows">Ring-buffer cap.</param>
    public void SaveDiagnostic(string source, int messageHash, string message, DateTime atUtc, DateTime lastAtUtc, int maxRows = 100)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO diagnostics (source, message_hash, message, count, at_utc, last_at_utc)
            VALUES (@source, @hash, @msg, 1, @at, @last)
            ON CONFLICT(source, message_hash) DO UPDATE SET
                count = count + 1,
                message = excluded.message,
                last_at_utc = excluded.last_at_utc";
        cmd.Parameters.AddWithValue("@source", source);
        cmd.Parameters.AddWithValue("@hash", messageHash);
        cmd.Parameters.AddWithValue("@msg", message);
        cmd.Parameters.AddWithValue("@at", atUtc.Ticks);
        cmd.Parameters.AddWithValue("@last", lastAtUtc.Ticks);
        cmd.ExecuteNonQuery();

        // Trim to newest maxRows. Same policy the in-memory LinkedList applied.
        using var trim = connection.CreateCommand();
        trim.CommandText = @"
            DELETE FROM diagnostics
             WHERE rowid NOT IN (
                 SELECT rowid FROM diagnostics ORDER BY last_at_utc DESC LIMIT @max
             )";
        trim.Parameters.AddWithValue("@max", maxRows);
        trim.ExecuteNonQuery();
    }

    /// <summary>
    /// Returns the persisted diagnostic entries, newest first. Populates the in-memory ring buffer
    /// on plugin startup so errors survive plugin updates / Jellyfin restarts.
    /// </summary>
    /// <param name="limit">Maximum entries to return.</param>
    /// <returns>Newest diagnostic entries first.</returns>
    public IReadOnlyList<(string Source, int MessageHash, string Message, int Count, DateTime AtUtc, DateTime LastAtUtc)> LoadDiagnostics(int limit = 100)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT source, message_hash, message, count, at_utc, last_at_utc FROM diagnostics ORDER BY last_at_utc DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);
        var rows = new List<(string, int, string, int, DateTime, DateTime)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            rows.Add((
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetInt32(3),
                // Diagnostics table has no per-row ID exposed here; pass 0 so the diagnostic
                // still records with a clear-enough table name. Malformed at_utc / last_at_utc
                // here would otherwise take down the Errors tab itself — the recursive-failure
                // guard the audit called out.
                RowHydration.SafeUtcFromTicks(reader.GetInt64(4), "diagnostics"),
                RowHydration.SafeUtcFromTicks(reader.GetInt64(5), "diagnostics")));
        }

        return rows;
    }

    /// <summary>Returns the total row count in the diagnostics table.</summary>
    /// <returns>Total persisted diagnostic entries.</returns>
    public int CountDiagnostics()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM diagnostics";
        var raw = cmd.ExecuteScalar();
        return raw is long l ? (int)l : 0;
    }

    /// <summary>Wipes the diagnostics table. Used by the Errors → Clear button and by Reset.</summary>
    public void ClearDiagnostics()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM diagnostics";
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes a specific diagnostic entry identified by <paramref name="source"/> and <paramref name="messageHash"/>.</summary>
    /// <param name="source">Diagnostic source tag.</param>
    /// <param name="messageHash">Hash of the pre-truncation message (see <see cref="Api.Diagnostics.Record"/>).</param>
    public void DeleteDiagnostic(string source, int messageHash)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM diagnostics WHERE source = $source AND message_hash = $hash";
        cmd.Parameters.AddWithValue("$source", source);
        cmd.Parameters.AddWithValue("$hash", messageHash);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Deletes every persisted diagnostic whose source starts with <paramref name="sourcePrefix"/>
    /// OR matches it exactly. Backs review finding P2-8.
    /// </summary>
    /// <param name="sourcePrefix">Prefix to match; rows whose source equals the prefix or begins with the prefix followed by <c>.</c> are deleted.</param>
    /// <returns>Number of rows deleted.</returns>
    public int DeleteDiagnosticsBySourcePrefix(string sourcePrefix)
    {
        if (string.IsNullOrEmpty(sourcePrefix))
        {
            return 0;
        }

        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        // `source = $exact` covers the base row; `source LIKE $prefix` covers dotted children.
        // SQL LIKE uses % as a wildcard, and callers can't inject one because we build the
        // pattern from a fixed prefix + literal ".%".
        cmd.CommandText = "DELETE FROM diagnostics WHERE source = $exact OR source LIKE $prefix";
        cmd.Parameters.AddWithValue("$exact", sourcePrefix);
        cmd.Parameters.AddWithValue("$prefix", sourcePrefix + ".%");
        return cmd.ExecuteNonQuery();
    }
}
