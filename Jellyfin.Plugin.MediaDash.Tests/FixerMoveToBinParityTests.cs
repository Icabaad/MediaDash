using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Regression guard: every fixer that recycles a file must feed that RecyclePath into either
/// FixResult.RecyclePath (primary file) or FixResult.AdditionalRecycled (sidecars / additional
/// originals). Without a captured path there's no HistoryEntry row, and the Recycle Bin tab
/// renders "no history" instead of a Restore button — the user report class that motivated the
/// AdditionalRecycled plumbing.
///
/// Bug-pattern audit round 2 (2026-09-19): the scan was previously scoped to just Fixers/ and
/// Api/ — any new top-level directory (ScheduledTasks/, Scanners/, etc.) would silently escape
/// the guard. Rewritten to walk every C# file under the plugin root except RecycleBin's own
/// definition file, with a per-file exclusion allow-list for the intentional fire-and-forget
/// sites (documented reasons only).
/// </summary>
public class FixerMoveToBinParityTests
{
    private static readonly Regex StatementLevel =
        new(@"^\s*_recycleBin\.MoveToBin\(", RegexOptions.Compiled);

    // Files that legitimately fire MoveToBin without immediately writing a HistoryEntry.
    // Content-based (file basename + line snippet) instead of line-based so unrelated line shifts
    // don't flake the test. Every addition here needs a documented reason in the leading comment.
    private static readonly (string File, string Snippet, string Why)[] AllowedFireAndForget =
    {
        (
            "FileBrowserController.cs",
            "_recycleBin.MoveToBin(full);",
            "Files-tab delete's manifest sidecar covers restore; user-initiated deletes shouldn't appear in the History tab (History is scoped to fix-run activity)."
        ),
    };

    // Directories under the plugin root that must NOT be walked. RecycleBin.cs itself is the
    // definition site — every self-reference there is bookkeeping (Consolidate / Restore / etc.).
    private static readonly HashSet<string> IgnoredFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "RecycleBin.cs",
    };

    [Fact]
    public void EveryMoveToBinCallInThePluginCapturesTheReturnOrIsExplicitlyAllowed()
    {
        // Whole-plugin scan (audit round 2): any new top-level dir gets caught automatically,
        // so a future ScheduledTasks/ recycle path or Scanners/ auto-recycle can't silently
        // escape by living outside the historical Fixers/ + Api/ pair.
        var pluginRoot = Path.Combine(RepoRoot(), "Jellyfin.Plugin.MediaDash");
        var offenders = FindFireAndForgetOffenders(pluginRoot)
            .Where(o => !AllowedFireAndForget.Any(a =>
                o.StartsWith(a.File + ":", StringComparison.Ordinal)
                && o.Contains(a.Snippet, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Fire-and-forget _recycleBin.MoveToBin(...) call(s). The return value must be captured "
            + "and routed to a HistoryEntry (via FixResult.RecyclePath / FixResult.AdditionalRecycled "
            + "for fixers, or a direct _db.AddHistory(new HistoryEntry { RecyclePath = ... }) for "
            + "controllers). If the call is intentionally fire-and-forget, add the file basename + "
            + "line snippet to AllowedFireAndForget with a documented reason. Offenders:\n  "
            + string.Join("\n  ", offenders));
    }

    private static List<string> FindFireAndForgetOffenders(string dir)
    {
        return Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(p => !IgnoredFiles.Contains(Path.GetFileName(p)))
            .SelectMany(path => File.ReadAllLines(path)
                .Select((line, i) => new { path = Path.GetFileName(path), lineNo = i + 1, line })
                .Where(x => StatementLevel.IsMatch(x.line))
                .Select(x => $"{x.path}:{x.lineNo}: {x.line.Trim()}"))
            .ToList();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Jellyfin.Plugin.MediaDash.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
