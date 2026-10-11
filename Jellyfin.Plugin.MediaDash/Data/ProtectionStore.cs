using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Owns the <c>protection_rules</c> table (schema v8). Consumed by
/// <see cref="Fixers.ProtectionService"/>, which caches the ruleset and does the actual
/// path/library matching at scan-emit and fix-execute time.
/// </summary>
public sealed class ProtectionStore
{
    private readonly MediaDashConnectionFactory _connectionFactory;

    /// <summary>Initializes a new instance of the <see cref="ProtectionStore"/> class.</summary>
    /// <param name="connectionFactory">Shared factory, same instance every store family uses.</param>
    public ProtectionStore(MediaDashConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
    }

    /// <summary>Returns every rule ordered by id.</summary>
    /// <returns>The full ruleset.</returns>
    public IReadOnlyList<ProtectionRule> GetAll()
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, kind, target, scanners_mask, fixers_mask, note, created_at_utc FROM protection_rules ORDER BY id";
        var results = new List<ProtectionRule>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new ProtectionRule
            {
                Id = reader.GetInt64(0),
                Kind = reader.GetString(1),
                Target = reader.GetString(2),
                ScannersMask = reader.GetString(3),
                FixersMask = reader.GetString(4),
                Note = reader.GetString(5),
                CreatedAtUtc = new DateTime(reader.GetInt64(6), DateTimeKind.Utc),
            });
        }

        return results;
    }

    /// <summary>Inserts a rule and returns its assigned id.</summary>
    /// <param name="rule">The rule to persist. <c>Id</c> is ignored.</param>
    /// <returns>The new row id.</returns>
    public long Add(ProtectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO protection_rules (kind, target, scanners_mask, fixers_mask, note, created_at_utc)
            VALUES (@kind, @target, @scanners, @fixers, @note, @created);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@kind", rule.Kind);
        cmd.Parameters.AddWithValue("@target", rule.Target);
        cmd.Parameters.AddWithValue("@scanners", rule.ScannersMask ?? string.Empty);
        cmd.Parameters.AddWithValue("@fixers", rule.FixersMask ?? string.Empty);
        cmd.Parameters.AddWithValue("@note", rule.Note ?? string.Empty);
        cmd.Parameters.AddWithValue("@created", rule.CreatedAtUtc.Ticks);
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Removes a rule by id.</summary>
    /// <param name="id">The row id to remove.</param>
    /// <returns><c>true</c> if a row was deleted.</returns>
    public bool Remove(long id)
    {
        using var connection = _connectionFactory.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM protection_rules WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }
}
