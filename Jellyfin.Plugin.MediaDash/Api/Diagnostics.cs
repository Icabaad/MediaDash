using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// Bounded ring buffer of recent plugin errors, surfaced by the Errors tab.
/// Callers push here from catch blocks so failures that would otherwise be invisible
/// (silent nulls from a system-stats sample, per-file scan failures, cancelled fixes)
/// show up in one place the admin can look at.
///
/// 02-C: state authority moved to the DI-owned
/// <see cref="Api.DiagnosticService"/> singleton. This static class stays as a
/// facade for the many legacy call sites (scanners/fixers/controller) that predate DI
/// injection. Every method forwards to <see cref="Plugin.AttachedDiagnosticService"/>
/// when present; before the service is constructed (early startup / test contexts
/// that don't spin up DI) the facade keeps a small local ring buffer so nothing is lost.
/// The first <see cref="Attach"/> call after DI wire-up drains the buffer into the service.
/// </summary>
public static class Diagnostics
{
    // Fallback state used before Plugin.AttachedDiagnosticService is set. Every real
    // deployment has the service attached within a few ms of plugin load, so this only
    // catches diagnostics recorded during the DI graph's own construction plus every
    // unit test that doesn't build a container.
    private const int FallbackMaxEntries = 100;
    private static readonly object FallbackLock = new();
    private static readonly LinkedList<DiagnosticEntry> FallbackEntries = new();
    private static Data.DiagnosticStore? _pendingStore;

    /// <summary>Attach the diagnostics store and rehydrate the ring buffer from persisted rows.</summary>
    /// <param name="store">The diagnostics store (05-C.5).</param>
    public static void Attach(Data.DiagnosticStore store)
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            service.Attach(store);
            return;
        }

        // Service not resolved yet — save the store reference so it can be attached on service
        // construction, and let the fallback buffer keep swallowing Record calls in the mean time.
        _pendingStore = store;
    }

    /// <summary>Records a diagnostic event.</summary>
    /// <param name="source">Short source label.</param>
    /// <param name="message">Human-readable description.</param>
    public static void Record(string source, string message)
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            service.Record(source, message);
            return;
        }

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var trimmed = message.Length > 800 ? message[..800] + "…" : message;
        var hash = Diagnostics_StableHash(message);
        var now = DateTime.UtcNow;
        lock (FallbackLock)
        {
            // Same dedup shape as DiagnosticService.Record — an identical (source, message) bumps
            // the existing entry's Count and moves it to the head, rather than adding a duplicate.
            LinkedListNode<DiagnosticEntry>? match = null;
            for (var node = FallbackEntries.First; node is not null; node = node.Next)
            {
                if (string.Equals(node.Value.Source, source, StringComparison.Ordinal)
                    && node.Value.MessageHash == hash
                    && string.Equals(node.Value.Message, trimmed, StringComparison.Ordinal))
                {
                    match = node;
                    break;
                }
            }

            if (match is not null)
            {
                var firstSeen = match.Value.AtUtc;
                var newCount = match.Value.Count + 1;
                FallbackEntries.Remove(match);
                FallbackEntries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = firstSeen,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = hash,
                    Count = newCount,
                });
            }
            else
            {
                FallbackEntries.AddFirst(new DiagnosticEntry
                {
                    AtUtc = now,
                    LastAtUtc = now,
                    Source = source,
                    Message = trimmed,
                    MessageHash = hash,
                    Count = 1,
                });
                while (FallbackEntries.Count > FallbackMaxEntries)
                {
                    FallbackEntries.RemoveLast();
                }
            }
        }
    }

    /// <summary>Returns the currently-buffered entries, newest first.</summary>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<DiagnosticEntry> Recent()
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            return service.Recent();
        }

        lock (FallbackLock)
        {
            return FallbackEntries.ToList();
        }
    }

    /// <summary>Reads the persisted diagnostics table directly.</summary>
    /// <returns>All persisted entries, newest first.</returns>
    public static IReadOnlyList<DiagnosticEntry> RecentAll()
    {
        var service = Plugin.AttachedDiagnosticService;
        return service is not null ? service.RecentAll() : Recent();
    }

    /// <summary>Total number of persisted diagnostic entries.</summary>
    /// <returns>Persisted row count, or the in-memory buffer size when the DB isn't attached.</returns>
    public static int PersistedCount()
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            return service.PersistedCount();
        }

        lock (FallbackLock)
        {
            return FallbackEntries.Count;
        }
    }

    /// <summary>Deterministic 32-bit FNV-1a hash.</summary>
    /// <param name="s">The string to hash.</param>
    /// <returns>A stable 32-bit hash.</returns>
    public static int StableStringHash(string s) => Diagnostics_StableHash(s);

    /// <summary>Empties the buffer and the persisted table.</summary>
    public static void Clear()
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            service.Clear();
            return;
        }

        lock (FallbackLock)
        {
            FallbackEntries.Clear();
        }
    }

    /// <summary>Startup-time cleanup for diagnostics the current build no longer emits.</summary>
    public static void PurgeObsolete()
    {
        var service = Plugin.AttachedDiagnosticService;
        service?.PurgeObsolete();
    }

    /// <summary>Removes every diagnostic entry matching source + message substring.</summary>
    /// <param name="source">Exact source tag to match.</param>
    /// <param name="messageSubstring">Substring the message must contain.</param>
    public static void RemoveMatching(string source, string messageSubstring)
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            service.RemoveMatching(source, messageSubstring);
            return;
        }

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(messageSubstring))
        {
            return;
        }

        lock (FallbackLock)
        {
            var node = FallbackEntries.First;
            while (node is not null)
            {
                var next = node.Next;
                if (string.Equals(node.Value.Source, source, StringComparison.Ordinal)
                    && node.Value.Message.Contains(messageSubstring, StringComparison.Ordinal))
                {
                    FallbackEntries.Remove(node);
                }

                node = next;
            }
        }
    }

    /// <summary>Removes every diagnostic whose source equals or begins with <paramref name="subsystem"/>.</summary>
    /// <param name="subsystem">Subsystem name.</param>
    public static void RemoveBySourcePrefix(string subsystem)
    {
        var service = Plugin.AttachedDiagnosticService;
        if (service is not null)
        {
            service.RemoveBySourcePrefix(subsystem);
            return;
        }

        if (string.IsNullOrEmpty(subsystem))
        {
            return;
        }

        var dotted = subsystem + ".";
        lock (FallbackLock)
        {
            var node = FallbackEntries.First;
            while (node is not null)
            {
                var next = node.Next;
                var src = node.Value.Source;
                if (string.Equals(src, subsystem, StringComparison.Ordinal)
                    || src.StartsWith(dotted, StringComparison.Ordinal))
                {
                    FallbackEntries.Remove(node);
                }

                node = next;
            }
        }
    }

    /// <summary>
    /// 02-C: called by <see cref="Api.DiagnosticService"/> after it installs itself as
    /// <see cref="Plugin.AttachedDiagnosticService"/>. Drains any diagnostics the fallback buffer
    /// captured before the service existed, and forwards a pending <see cref="Attach"/> DB.
    /// </summary>
    /// <param name="service">The freshly-constructed service to hand off state to.</param>
    internal static void DrainFallbackTo(DiagnosticService service)
    {
        List<DiagnosticEntry> snapshot;
        lock (FallbackLock)
        {
            snapshot = FallbackEntries.ToList();
            FallbackEntries.Clear();
        }

        // Replay from oldest → newest so the resulting ring buffer preserves relative order.
        for (var i = snapshot.Count - 1; i >= 0; i--)
        {
            service.Record(snapshot[i].Source, snapshot[i].Message);
        }

        var pending = _pendingStore;
        _pendingStore = null;
        if (pending is not null)
        {
            service.Attach(pending);
        }
    }

    private static int Diagnostics_StableHash(string s)
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
