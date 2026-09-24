using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Review P1-1 regression: the swap sidecar preserved after a failed final rename must be
/// renamed OUT of the <c>*.mediadash.new*</c> sweep-pattern set so the sweep at end-of-fix-run
/// can't delete the user's only remaining copy of the content. Without this rename, permanent
/// disposal + rename failure = data loss.
/// </summary>
public sealed class SwapRecoveryHelperTests
{
    [Fact]
    public void IsRecoveryCopy_TrueForRecoveryTaggedName()
    {
        var path = "/lib/Movies/example.mediadash-recover-20260918-140500.mkv";
        Assert.True(SwapRecoveryHelper.IsRecoveryCopy(path));
    }

    [Fact]
    public void IsRecoveryCopy_FalseForPlainSwapSidecar()
    {
        // The whole point of the rename is to move OUT of these sweep-matched patterns.
        Assert.False(SwapRecoveryHelper.IsRecoveryCopy("/lib/Movies/example.abc.mediadash.new.mkv"));
        Assert.False(SwapRecoveryHelper.IsRecoveryCopy("/lib/Movies/example.abc.mediadash.tmp.mkv"));
        Assert.False(SwapRecoveryHelper.IsRecoveryCopy("/lib/Movies/example.mkv"));
    }

    [Fact]
    public void IsRecoveryCopy_FalseForEmptyOrNull()
    {
        Assert.False(SwapRecoveryHelper.IsRecoveryCopy(string.Empty));
        Assert.False(SwapRecoveryHelper.IsRecoveryCopy(null!));
    }

    [Fact]
    public void TryPreserve_RenamesToRecoveryTaggedSibling()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-swap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var swapPath = Path.Combine(tempDir, "video.abc.mediadash.new.mkv");
            var originalPath = Path.Combine(tempDir, "video.mkv");
            File.WriteAllBytes(swapPath, new byte[] { 1, 2, 3 });

            var preserved = SwapRecoveryHelper.TryPreserve(swapPath, originalPath);

            Assert.NotEqual(swapPath, preserved);
            Assert.False(File.Exists(swapPath));
            Assert.True(File.Exists(preserved));
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preserved));
            Assert.EndsWith(".mkv", preserved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryPreserve_ReturnsOriginalPathOnFailure()
    {
        // Non-existent source — rename throws, helper returns the input unchanged so the
        // diagnostic message still points at a real (if doomed) location.
        var swapPath = Path.Combine(Path.GetTempPath(), "mediadash-does-not-exist-" + Guid.NewGuid().ToString("N") + ".mkv");
        var originalPath = Path.Combine(Path.GetTempPath(), "target.mkv");

        var preserved = SwapRecoveryHelper.TryPreserve(swapPath, originalPath);

        Assert.Equal(swapPath, preserved);
    }

    [Fact]
    public void TryPreserve_DirectoryAtRecoveryDestination_UsesUniqueName()
    {
        // Bug-pattern audit round 4 P-03 (2026-09-19): the pre-fix collision loop only called
        // File.Exists, so a directory occupying the recovery destination fell through as "free"
        // and File.Move failed silently. The outer catch returned swapPath — the sweep then
        // deleted the survivor. Now Directory.Exists is checked too, so the helper picks a
        // suffixed name.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-swap-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var swapPath = Path.Combine(tempDir, "clip.abc.mediadash.new.mkv");
            var originalPath = Path.Combine(tempDir, "clip.mkv");
            File.WriteAllBytes(swapPath, new byte[] { 9, 9, 9 });

            // Seed a DIRECTORY at every stamp variant we can predict — the current-second
            // recovery name and the "-1" suffix. Force the helper to keep incrementing.
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(Path.Combine(tempDir, "clip.mediadash-recover-" + stamp + ".mkv"));

            var preserved = SwapRecoveryHelper.TryPreserve(swapPath, originalPath);

            Assert.True(File.Exists(preserved), "preserved path exists as a file (not the seeded directory)");
            Assert.False(File.Exists(swapPath), "swap moved out of the sweep pattern");
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preserved));
            Assert.NotEqual(swapPath, preserved);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryPreserve_LongUtf8Basename_FallsBackToHashName()
    {
        // Bug-pattern audit round 4 P-03 case (2): a real 244-byte UTF-8 basename fits on disk
        // under NAME_MAX (255), but appending ".mediadash-recover-YYYYMMDD-HHMMSS.mkv" would
        // overflow. Pre-fix: File.Move threw ENAMETOOLONG, outer catch returned swapPath, sweep
        // deleted it. Post-fix: the builder falls back to a hash-based name that fits.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-swap-long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 122 chars of Cyrillic (2 bytes each in UTF-8) = 244 bytes basename. Under NAME_MAX
            // for the file itself (255 budget) but well over the 240-byte budget once the marker
            // + timestamp + extension is appended, so the hash-fallback branch is exercised.
            var longBase = new string('л', 122);
            var originalPath = Path.Combine(tempDir, longBase + ".mkv");
            var shortSwap = Path.Combine(tempDir, "swap-src.mkv");
            File.WriteAllBytes(shortSwap, new byte[] { 5, 5, 5 });

            var preserved = SwapRecoveryHelper.TryPreserve(shortSwap, originalPath);

            // Under the hash-fallback branch the filename starts with "mediadash-recover-"
            // rather than the (too-long) original basename. This is the crucial signal — the
            // pre-fix code would have kept the full 244-byte basename and File.Move would have
            // thrown ENAMETOOLONG, sending the caller down the total-failure branch.
            var preservedName = Path.GetFileName(preserved);
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(preservedName) <= 240,
                "hash-fallback name fits under NAME_MAX budget (got " + System.Text.Encoding.UTF8.GetByteCount(preservedName) + ")");
            // The RecoveryMarker infix must survive the hash-fallback branch — the sweep
            // negative check keys off it regardless of which naming path produced the file.
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preserved));
            // Hash prefix is the tell-tale of the fallback branch (vs. the full-basename +
            // marker path used for short names).
            Assert.DoesNotContain(new string('л', 122), preservedName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
