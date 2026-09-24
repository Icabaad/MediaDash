using System;
using System.IO;
using Jellyfin.Plugin.MediaDash.Api;
using Jellyfin.Plugin.MediaDash.Data;
using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Bug-pattern audit 2026-09-19: <see cref="RecycleBin.MoveToBin"/> has always accepted
/// directories (orphan cleanup recycles empty per-title folders; Files-tab folder deletes route
/// through the same path), but <see cref="RecycleBin.ListContents"/> only enumerated files, so
/// recycled trees were invisible in the Recycle bin tab and <see cref="HistoryDto.CanRestore"/>
/// returned false on directory history rows. These tests pin the pieces that don't require the
/// full DI-heavy RecycleBin instance: the recursive size helper (used for per-row bin sizes so
/// they match GetContents' totals) and the DTO's dual-existence check (used by the UI to render
/// the Restore button).
/// </summary>
public sealed class RecycleBinDirectoryVisibilityTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), "mediadash-dirvis-" + Guid.NewGuid().ToString("N"));

    public RecycleBinDirectoryVisibilityTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void DirectorySizeBytes_SumsRecursively()
    {
        // Recycled tree: two subfolders, three files, 8 + 16 + 32 = 56 bytes. The bin's per-row
        // size must equal what the top-level GetContents scan sums (also recursive) or the UI
        // shows a per-row size that doesn't match the totals column — the exact confused-user
        // report we're trying to prevent.
        var root = Path.Combine(_scratch, "movie-folder");
        Directory.CreateDirectory(Path.Combine(root, "extras"));
        Directory.CreateDirectory(Path.Combine(root, "sub", "deeper"));
        File.WriteAllBytes(Path.Combine(root, "movie.mkv"), new byte[8]);
        File.WriteAllBytes(Path.Combine(root, "extras", "trailer.mkv"), new byte[16]);
        File.WriteAllBytes(Path.Combine(root, "sub", "deeper", "still-counted.srt"), new byte[32]);

        Assert.Equal(56, RecycleBin.DirectorySizeBytes(root));
    }

    [Fact]
    public void DirectorySizeBytes_ReturnsZeroForMissingPath()
    {
        // Benign race: a purge / restore removed the directory between ListContents enumerating
        // it and this helper walking it. Fail closed at 0 rather than throwing (the surrounding
        // catch would swallow it anyway, but 0 is the honest answer for "nothing there").
        var gone = Path.Combine(_scratch, "does-not-exist-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(0, RecycleBin.DirectorySizeBytes(gone));
    }

    [Fact]
    public void CanRestore_TrueForRecycledFile()
    {
        var recyclePath = Path.Combine(_scratch, "recycled-file.mkv");
        File.WriteAllBytes(recyclePath, new byte[4]);
        var entry = new HistoryEntry
        {
            Restored = false,
            RecyclePath = recyclePath,
            Path = "/lib/original.mkv",
            Type = IssueType.Playability
        };

        Assert.True(HistoryDto.FromEntry(entry).CanRestore);
    }

    [Fact]
    public void CanRestore_TrueForRecycledDirectory()
    {
        // The regression: orphan cleanup / Files-tab folder deletes recycle a directory; the
        // History tab was hiding the Restore button because File.Exists returned false on the
        // directory even though the RestoreFromHistory endpoint would happily restore it.
        var recyclePath = Path.Combine(_scratch, "recycled-folder");
        Directory.CreateDirectory(recyclePath);
        File.WriteAllBytes(Path.Combine(recyclePath, "child.mkv"), new byte[2]);
        var entry = new HistoryEntry
        {
            Restored = false,
            RecyclePath = recyclePath,
            Path = "/lib/originalFolder",
            Type = IssueType.OrphanedDebris
        };

        Assert.True(HistoryDto.FromEntry(entry).CanRestore);
    }

    [Fact]
    public void CanRestore_FalseWhenAlreadyRestored()
    {
        // Restored rows must not offer a second Restore — the file is already back in place, and
        // the recycle-bin copy has been consumed. Blocking here matches the RestoreFromHistory
        // endpoint's own 409 for already-restored entries.
        var recyclePath = Path.Combine(_scratch, "twice-restored.mkv");
        File.WriteAllBytes(recyclePath, new byte[1]);
        var entry = new HistoryEntry
        {
            Restored = true,
            RecyclePath = recyclePath,
            Path = "/lib/back-in-place.mkv",
            Type = IssueType.Playability
        };

        Assert.False(HistoryDto.FromEntry(entry).CanRestore);
    }

    [Fact]
    public void CanRestore_FalseWhenBinPathIsGone()
    {
        // Bin was emptied between the fix and the user viewing history — recycle path no longer
        // resolves to a file OR a directory. The user needs to see the row (as an audit trail)
        // but without a Restore button that will 409.
        var gone = Path.Combine(_scratch, "purged-" + Guid.NewGuid().ToString("N"));
        var entry = new HistoryEntry
        {
            Restored = false,
            RecyclePath = gone,
            Path = "/lib/original.mkv",
            Type = IssueType.Playability
        };

        Assert.False(HistoryDto.FromEntry(entry).CanRestore);
    }
}
