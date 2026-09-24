using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// DI-owned singleton that carries the bounded, deduplicated ring buffer of recent plugin
/// errors and the write-through to the persisted <c>diagnostics</c> SQLite table. The static
/// <see cref="Api.Diagnostics"/> class becomes a thin forwarder to this service via
/// <see cref="Plugin.AttachedDiagnosticService"/> — startup callers that predate DI can still
/// call the static entry points, but the state authoritatively lives here.
///
/// Design invariants:
/// <list type="bullet">
///   <item>Every buffer mutation guards on <c>_lock</c>. Persistence I/O runs OUTSIDE the lock
///     so a slow SQLite write can never deadlock a Record caller.</item>
///   <item>DB write failure is best-effort (logger-only fallback). The service NEVER recurses
///     back into itself with a diagnostic that says "persisting a diagnostic failed" — that
///     would inflate the exact table the caller is trying to write to.</item>
///   <item>Bounded dedup by <c>(source, messageHash)</c> using FNV-1a 32-bit hash. Hash is
///     stable across processes and machines so a restart does not double-count entries.</item>
///   <item>Ring buffer size + persisted cap are class-level constants matching the pre-migration
///     values (100 in-memory, 5000 persisted) so <see cref="Api.Diagnostics"/>-facing behaviour
///     is preserved.</item>
/// </list>
/// </summary>
public sealed class DiagnosticService
{
    // Pre-migration constants preserved so the /Errors payload doesn't change shape.
    internal const int MaxEntries = 100;
    internal const int MaxPersisted = 5000;

    private readonly object _lock = new();
    private readonly LinkedList<DiagnosticEntry> _entries = new();
    private Data.DiagnosticStore? _diag;

    /// <summary>
    /// Initializes a new instance of the <see cref="DiagnosticService"/> class and installs itself
    /// as <see cref="Plugin.AttachedDiagnosticService"/> so the legacy <see cref="Api.Diagnostics"/>
    /// static facade forwards to this instance from now on. Idempotent — repeated DI resolutions
    /// re-assign the same reference. Test-instantiations (<c>new DiagnosticService()</c> in a
    /// unit test that does not want to affect the shared static bridge) can clear the bridge
    /// themselves in a fixture teardown.
    /// </summary>
    public DiagnosticService()
    {
        Plugin.AttachedDiagnosticService = this;
        // 02-C: drain anything the static Diagnostics facade buffered before DI resolved
        // the service, then hand off any pending Attach(db) call. Order matters — the drain
        // has to happen INSIDE our own state, not against a nulled bridge.
        Diagnostics.DrainFallbackTo(this);
    }

    /// <summary>Attaches the diagnostics store (05-C.5) and rehydrates the ring buffer from persisted rows.</summary>
    /// <param name="store">The diagnostics store. Rows in the <c>diagnostics</c> table become the initial buffer contents.</param>
    public void Attach(Data.DiagnosticStore store)
    {
        lock (_lock)
        {
            _diag = store;
            _entries.Clear();
            try
            {
                foreach (var row in store.LoadDiagnostics(MaxEntries))
                {
                    _entries.AddLast(new DiagnosticEntry
                    {
                        AtUtc = row.AtUtc,
                        LastAtUtc = row.LastAtUtc,
                        Source = row.Source,
                        Message = row.Message,
                        MessageHash = row.MessageHash,
                        Count = row.Count,
                    });
                }
            }
            catch (Exception)
            {
                // Fresh install / disk hiccup / schema not yet migrated — start empty and let
                // subsequent Record calls repopulate.
            }
        }
    }

    /// <summary>Records a diagnostic event with dedup + persistence.</summary>
    /// <param name="source">Short source label (e.g., "SystemStats.Linux", "PlayabilityScanner").</param>
    /// <param name="message">Human-readable description of what went wrong.</param>
    public void Record(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var trimmed = message.Length > 800 ? message[..800] + "…" : message;
        // Dedup key hashes the *pre-truncation* message so two different long errors that share
        // the truncation prefix don't get collapsed into one entry with a misleading Count.
        var messageHash = StableStringHash(message);
        var now = DateTime.UtcNow;
        DateTime firstSeen;
        int newCount;
        lock (_lock)
        {
            LinkedListNode<DiagnosticEntry>? match = null;
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Source, source, StringComparison.Ordinal)
                    && node.Value.MessageHash == messageHash
                    && string.Equals(node.Value.Message, trimmed, StringComparison.Ordinal))
                {
                    match = node;
                    break;
                }
            }

            if (match is not null)
            {
                firstSeen = match.Value.AtUtc;
                newCount = match.Value.Count + 1;
                _entries.Remove(match);
                _entries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = firstSeen,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = messageHash,
                    Count = newCount,
                });
            }
            else
            {
                firstSeen = now;
                newCount = 1;
                _entries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = firstSeen,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = messageHash,
                    Count = newCount,
                });
                while (_entries.Count > MaxEntries)
                {
                    _entries.RemoveLast();
                }
            }
        }

        // Persistence outside the lock — SQLite has its own locking and holding _lock across a
        // disk write can stall every concurrent Record. Failure is silent (logger would just
        // recurse into this table — the exact recursion 02-C explicitly guards against).
        var db = _diag;
        if (db is not null)
        {
            try
            {
                db.SaveDiagnostic(source, messageHash, trimmed, firstSeen, now, MaxPersisted);
            }
            catch (Exception)
            {
                // Best-effort. The in-memory copy stays authoritative for this session and
                // will re-persist on the next successful Record.
            }
        }
    }

    /// <summary>Returns the currently-buffered entries, newest first.</summary>
    /// <returns>The entries.</returns>
    public IReadOnlyList<DiagnosticEntry> Recent()
    {
        lock (_lock)
        {
            return _entries.ToList();
        }
    }

    /// <summary>Reads the persisted diagnostics table directly, bypassing the in-memory ring buffer.</summary>
    /// <returns>All persisted entries, newest first; falls back to <see cref="Recent"/> when the DB is unattached or fails.</returns>
    public IReadOnlyList<DiagnosticEntry> RecentAll()
    {
        var db = _diag;
        if (db is null)
        {
            return Recent();
        }

        try
        {
            var rows = db.LoadDiagnostics(MaxPersisted);
            var list = new List<DiagnosticEntry>(rows.Count);
            foreach (var row in rows)
            {
                list.Add(new DiagnosticEntry
                {
                    AtUtc = row.AtUtc,
                    LastAtUtc = row.LastAtUtc,
                    Source = row.Source,
                    Message = row.Message,
                    MessageHash = row.MessageHash,
                    Count = row.Count,
                });
            }

            return list;
        }
        catch (Exception)
        {
            return Recent();
        }
    }

    /// <summary>Total number of persisted diagnostic entries.</summary>
    /// <returns>Persisted row count, or the in-memory buffer size when the DB isn't attached.</returns>
    public int PersistedCount()
    {
        var db = _diag;
        if (db is null)
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }

        try
        {
            return db.CountDiagnostics();
        }
        catch (Exception)
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Empties the in-memory buffer and the persisted table.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
        }

        var db = _diag;
        if (db is not null)
        {
            try
            {
                db.ClearDiagnostics();
            }
            catch (Exception)
            {
                // Best-effort — see Record's rationale.
            }
        }
    }

    /// <summary>Removes every diagnostic whose source equals <paramref name="source"/> AND whose message contains <paramref name="messageSubstring"/>.</summary>
    /// <param name="source">Exact source tag to match.</param>
    /// <param name="messageSubstring">Substring the message must contain.</param>
    public void RemoveMatching(string source, string messageSubstring)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(messageSubstring))
        {
            return;
        }

        var doomed = new List<(string Source, int Hash)>();
        lock (_lock)
        {
            var node = _entries.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Source, source, StringComparison.Ordinal)
                    && node.Value.Message.Contains(messageSubstring, StringComparison.Ordinal))
                {
                    doomed.Add((node.Value.Source, node.Value.MessageHash));
                    _entries.Remove(node);
                }

                node = next;
            }
        }

        var db = _diag;
        if (db is null || doomed.Count == 0)
        {
            return;
        }

        foreach (var (src, hash) in doomed)
        {
            try
            {
                db.DeleteDiagnostic(src, hash);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>Removes every diagnostic whose source equals or begins with <paramref name="subsystem"/>.</summary>
    /// <param name="subsystem">Subsystem name (base source).</param>
    public void RemoveBySourcePrefix(string subsystem)
    {
        if (string.IsNullOrEmpty(subsystem))
        {
            return;
        }

        var dotted = subsystem + ".";
        lock (_lock)
        {
            var node = _entries.First;
            while (node is not null)
            {
                var next = node.Next;
                var src = node.Value.Source;
                if (string.Equals(src, subsystem, StringComparison.Ordinal)
                    || src.StartsWith(dotted, StringComparison.Ordinal))
                {
                    _entries.Remove(node);
                }

                node = next;
            }
        }

        var db = _diag;
        if (db is null)
        {
            return;
        }

        try
        {
            db.DeleteDiagnosticsBySourcePrefix(subsystem);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Startup-time cleanup for diagnostics the current build no longer emits.</summary>
    public void PurgeObsolete()
    {
        RemoveMatching("SmartHealth.Wmi", "No MSFT_StorageReliabilityCounter association found");
        RemoveMatching("SmartHealth.Wmi", "PowerShell counter fallback ran for");
        RemoveMatching("RecycleBin.LegacyBatchNeedsReview", "Legacy-looking recycle batch");
    }

    /// <summary>Deterministic 32-bit FNV-1a hash used as the persisted dedup key.</summary>
    /// <param name="s">The string to hash.</param>
    /// <returns>A stable 32-bit hash suitable for the diagnostics dedup key.</returns>
    public static int StableStringHash(string s)
    {
        unchecked
        {
            const int Offset = (int)2166136261;
            const int Prime = 16777619;
            var hash = Offset;
            foreach (var c in s)
            {
                hash = (hash ^ c) * Prime;
            }

            return hash;
        }
    }
}
