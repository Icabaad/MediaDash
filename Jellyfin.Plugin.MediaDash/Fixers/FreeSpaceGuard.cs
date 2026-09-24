using System;
using System.IO;

namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Shared free-disk-space precondition used before any encode/remux/repair mutation. Backs
/// CLAUDE.md safety invariant 5 (transcode fixers need at least 2× source size available before
/// beginning). Fails closed when the target drive can't be resolved so a missing DriveInfo can't
/// be interpreted as "plenty of space." Overflow-safe against pathological source sizes.
/// </summary>
internal static class FreeSpaceGuard
{
    /// <summary>
    /// Returns true when the volume holding <paramref name="path"/> has at least
    /// <paramref name="multiplier"/> × source size available. Callers use multiplier=2 for the
    /// standard transcode/remux gate; the repair ladder passes 3 when a re-encode rung is enabled.
    /// </summary>
    /// <param name="path">Full path to the source file. Must exist and be resolvable to a drive.</param>
    /// <param name="multiplier">Space headroom multiplier (e.g. 2 for `2 × source`).</param>
    /// <returns>True when free space is at least <c>multiplier × sourceSize</c>. False if the drive
    /// can't be identified, the file is missing, or the volume is short.</returns>
    public static bool HasEnoughFreeSpace(string path, int multiplier)
    {
        if (multiplier <= 0)
        {
            return true;
        }

        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists)
            {
                return false;
            }

            var root = Path.GetPathRoot(fi.FullName);
            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);
            // Overflow-safe: compare via division instead of `fi.Length * multiplier`, which would
            // wrap for sources larger than long.MaxValue / multiplier (~4.6 EB at multiplier=2 —
            // unrealistic today, but "unrealistic" is exactly how overflow bugs get shipped).
            return drive.AvailableFreeSpace / multiplier >= fi.Length;
        }
        catch (IOException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
