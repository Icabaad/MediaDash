using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Root-cause audit 2026-09-19 R02+R03: the end-of-fix-run sidecar sweep infers disposability
/// from filename alone. A cover-art crash between the original's disposal and the final rename
/// leaves the ONLY remaining copy at <c>album.mp3.mediadash.swap</c> which the sweep then
/// destroys. TranscodeFixer/TrackFixer's <c>.mediadash.new*</c> share the same failure shape.
/// When the reactive <see cref="SwapRecoveryHelper.TryPreserve"/> total-failure branch fires
/// it returns the sweep-doomed path — the diagnostic points at a file the sweep then eats.
///
/// The fix shifts protection from reactive-after-failure to proactive-before-dispose. Fixers
/// call <see cref="SwapRecoveryHelper.TryPromoteBeforeDispose"/> to rename the verified swap
/// to a <c>*.mediadash-recover-*</c> name OUTSIDE the sweep pattern set BEFORE disposing the
/// original. Failure of the promote returns false and the caller aborts without disposing —
/// original stays intact, sweep can safely delete the swap. Success gives the caller the
/// recovery path to complete the swap from.
/// </summary>
public sealed class SweepOrphanProtectionTests
{
    [Fact]
    public void TryPromoteBeforeDispose_Success_MovesFileOutOfSweepPattern()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-promote-ok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var swapPath = Path.Combine(tempDir, "album.mp3.mediadash.swap");
            var originalPath = Path.Combine(tempDir, "album.mp3");
            File.WriteAllBytes(swapPath, new byte[] { 1, 2, 3 });

            var ok = SwapRecoveryHelper.TryPromoteBeforeDispose(swapPath, originalPath, out var preservedPath);

            Assert.True(ok);
            Assert.NotNull(preservedPath);
            Assert.False(File.Exists(swapPath), "swap moved out of the sweep pattern");
            Assert.True(File.Exists(preservedPath!));
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preservedPath!));
            Assert.EndsWith(".mp3", preservedPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryPromoteBeforeDispose_MissingSource_ReturnsFalse()
    {
        // If the swap file has already vanished (concurrent process, filesystem hiccup), promote
        // has nothing to protect — signal failure so the caller aborts dispose. The original is
        // still intact; safer to fail the fix than to dispose without a verified promotion.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-promote-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var swapPath = Path.Combine(tempDir, "gone.mediadash.swap");
            var originalPath = Path.Combine(tempDir, "gone.mp3");
            // deliberately no swap file created

            var ok = SwapRecoveryHelper.TryPromoteBeforeDispose(swapPath, originalPath, out var preservedPath);

            Assert.False(ok);
            Assert.Null(preservedPath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryPromoteBeforeDispose_LongUtf8Basename_HashFallbackKeepsPromoteSuccessful()
    {
        // R03 edge case: 244-byte UTF-8 basename overflows NAME_MAX after appending the recovery
        // marker + timestamp. Pre-fix flow: File.Move ENAMETOOLONG → catch → return swapPath
        // (sweep-doomed). New flow: BuildRecoveryPath's hash-fallback branch produces a bounded
        // name that fits, promotion succeeds, dispose is safe.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-promote-long-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var longBase = new string('л', 122); // 244 UTF-8 bytes
            var originalPath = Path.Combine(tempDir, longBase + ".mkv");
            var swapPath = Path.Combine(tempDir, "swap.mediadash.swap");
            File.WriteAllBytes(swapPath, new byte[] { 5 });

            var ok = SwapRecoveryHelper.TryPromoteBeforeDispose(swapPath, originalPath, out var preservedPath);

            Assert.True(ok);
            Assert.NotNull(preservedPath);
            Assert.True(File.Exists(preservedPath!));
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preservedPath!));
            Assert.True(
                System.Text.Encoding.UTF8.GetByteCount(Path.GetFileName(preservedPath!)) <= 240,
                "hash-fallback name fits under NAME_MAX budget");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryPromoteBeforeDispose_DirectoryAtRecoveryDestination_UsesUniqueName()
    {
        // Collision loop must handle a directory occupying the recovery name — otherwise File.Move
        // fails silently, catch returns null, caller thinks promotion failed and aborts a fix that
        // would have been safe. The collision loop probes both File.Exists AND Directory.Exists.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-promote-dir-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var swapPath = Path.Combine(tempDir, "clip.mediadash.swap");
            var originalPath = Path.Combine(tempDir, "clip.mkv");
            File.WriteAllBytes(swapPath, new byte[] { 9 });

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            Directory.CreateDirectory(Path.Combine(tempDir, "clip.mediadash-recover-" + stamp + ".mkv"));

            var ok = SwapRecoveryHelper.TryPromoteBeforeDispose(swapPath, originalPath, out var preservedPath);

            Assert.True(ok);
            Assert.NotNull(preservedPath);
            Assert.True(File.Exists(preservedPath!));
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preservedPath!));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void PromotedRecoveryFileSurvivesSweepPattern()
    {
        // Contract check: whatever name TryPromoteBeforeDispose picks must be recognised by
        // IsRecoveryCopy — the sweep pattern skip depends on it. Belt-and-braces so a future
        // rename of the marker constant doesn't silently break sweep protection.
        var tempDir = Path.Combine(Path.GetTempPath(), "mediadash-promote-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var swapPath = Path.Combine(tempDir, "song.mediadash.swap");
            var originalPath = Path.Combine(tempDir, "song.mp3");
            File.WriteAllBytes(swapPath, new byte[] { 1 });

            var ok = SwapRecoveryHelper.TryPromoteBeforeDispose(swapPath, originalPath, out var preservedPath);
            Assert.True(ok);
            Assert.True(SwapRecoveryHelper.IsRecoveryCopy(preservedPath!));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
