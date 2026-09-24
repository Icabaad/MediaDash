using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Root-cause audit 2026-09-19 R07: "Queue a duplicate issue whose recorded keeper is now zero
/// bytes and whose proposed loser is nonempty. Actual DuplicateFixer returns success, deletes
/// the nonempty file and leaves the zero-byte keeper." Fix-time validation checked keeper
/// existence but not integrity — the scanner's ranking premise goes stale by execution time.
/// Extracted <see cref="DuplicateFixer.KeeperStillViable"/> tightens the gate: keeper must be
/// non-zero AND, when the scanner recorded a size in DetailsJson, must not have shrunk by more
/// than half (truncation between scan and fix invalidates the keeper choice).
/// </summary>
public class R07KeeperViabilityTests
{
    [Fact]
    public void KeeperStillViable_ZeroByteKeeper_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-r07-zero-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keeper = Path.Combine(tempDir, "keeper.mkv");
            File.WriteAllBytes(keeper, Array.Empty<byte>());
            var details = "{\"keeperPath\":\"" + keeper.Replace("\\", "\\\\") + "\",\"keeper\":{\"Size\":123456789}}";

            var ok = DuplicateFixer.KeeperStillViable(keeper, details, out var reason);

            Assert.False(ok);
            Assert.NotNull(reason);
            Assert.Contains("0 bytes", reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void KeeperStillViable_KeeperTruncatedBelowHalfOfRecorded_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-r07-trunc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keeper = Path.Combine(tempDir, "keeper.mkv");
            File.WriteAllBytes(keeper, new byte[100_000]); // 100 KB now
            var details = "{\"keeperPath\":\"" + keeper.Replace("\\", "\\\\") + "\",\"keeper\":{\"Size\":1000000}}"; // 1 MB at scan

            var ok = DuplicateFixer.KeeperStillViable(keeper, details, out var reason);

            Assert.False(ok);
            Assert.NotNull(reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void KeeperStillViable_HealthyKeeperMatchingRecorded_ReturnsTrue()
    {
        // Regression guard: the gate must not fire for a keeper the scanner correctly picked.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-r07-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keeper = Path.Combine(tempDir, "keeper.mkv");
            File.WriteAllBytes(keeper, new byte[500_000]); // 500 KB
            var details = "{\"keeperPath\":\"" + keeper.Replace("\\", "\\\\") + "\",\"keeper\":{\"Size\":500000}}";

            var ok = DuplicateFixer.KeeperStillViable(keeper, details, out var reason);

            Assert.True(ok);
            Assert.Null(reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void KeeperStillViable_NonZeroButNoRecordedSize_ReturnsTrue()
    {
        // DetailsJson malformed or missing keeper.Size — fall back to size > 0 as the only check.
        // Doesn't false-positive: any non-zero file is at least plausibly valid without further
        // evidence.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-r07-nosize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keeper = Path.Combine(tempDir, "keeper.mkv");
            File.WriteAllBytes(keeper, new byte[10_000]);
            var details = "{\"keeperPath\":\"" + keeper.Replace("\\", "\\\\") + "\"}"; // no keeper block

            var ok = DuplicateFixer.KeeperStillViable(keeper, details, out var reason);

            Assert.True(ok);
            Assert.Null(reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void KeeperStillViable_MissingKeeperFile_ReturnsFalse()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-r07-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keeper = Path.Combine(tempDir, "vanished.mkv");
            var details = "{\"keeperPath\":\"" + keeper.Replace("\\", "\\\\") + "\"}";

            var ok = DuplicateFixer.KeeperStillViable(keeper, details, out var reason);

            Assert.False(ok);
            Assert.NotNull(reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void KeeperStillViable_MalformedDetailsJson_UsesSizeOnly()
    {
        // Defence-in-depth: a corrupt DetailsJson mustn't take down the fixer. Fall back to the
        // filesystem-only check (size > 0).
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-r07-bad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var keeper = Path.Combine(tempDir, "keeper.mkv");
            File.WriteAllBytes(keeper, new byte[10_000]);

            var ok = DuplicateFixer.KeeperStillViable(keeper, "{not valid json", out var reason);
            Assert.True(ok);
            Assert.Null(reason);

            // Root is an array, not an object — the ValueKind guards must not throw.
            ok = DuplicateFixer.KeeperStillViable(keeper, "[]", out reason);
            Assert.True(ok);
            Assert.Null(reason);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
