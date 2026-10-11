using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Pure-computation tests for <see cref="StatusQueryService.ComputeLowSpaceWarning(long, long, string, int)"/>
/// (i-bl-11). No DriveInfo, no plugin config — the overload takes plain numbers so every branch is
/// covered without touching the filesystem.
/// </summary>
public class LowSpaceWarningTests
{
    private const long OneGb = 1024L * 1024L * 1024L;

    [Fact]
    public void Ntfs_Under90Percent_ReturnsNull()
    {
        // 100 GB drive, 15 GB free → 85 % used, below the default 90 % threshold.
        var msg = StatusQueryService.ComputeLowSpaceWarning(15 * OneGb, 100 * OneGb, "NTFS", 90);
        Assert.Null(msg);
    }

    [Fact]
    public void Ntfs_At90Percent_ReturnsWarning()
    {
        // 10 GB free on 100 GB → 90 % used exactly. At the threshold means "cross".
        var msg = StatusQueryService.ComputeLowSpaceWarning(10 * OneGb, 100 * OneGb, "NTFS", 90);
        Assert.NotNull(msg);
        Assert.Contains("90% full", msg);
    }

    [Fact]
    public void Zfs_Auto_Tightens_By_Five_Percent()
    {
        // 86 % used → below NTFS's 90 % threshold but above ZFS's effective 85 %.
        var msg = StatusQueryService.ComputeLowSpaceWarning(14 * OneGb, 100 * OneGb, "zfs", 90);
        Assert.NotNull(msg);
        Assert.Contains("86% full", msg);
    }

    [Fact]
    public void Btrfs_Auto_Tightens_By_Five_Percent()
    {
        var msg = StatusQueryService.ComputeLowSpaceWarning(14 * OneGb, 100 * OneGb, "btrfs", 90);
        Assert.NotNull(msg);
    }

    [Fact]
    public void Btrfs_Below_Tightened_Threshold_ReturnsNull()
    {
        // 80 % used on btrfs: below the tightened 85 % threshold.
        var msg = StatusQueryService.ComputeLowSpaceWarning(20 * OneGb, 100 * OneGb, "btrfs", 90);
        Assert.Null(msg);
    }

    [Fact]
    public void ZeroThreshold_DisablesTheWarning()
    {
        var msg = StatusQueryService.ComputeLowSpaceWarning(1 * OneGb, 100 * OneGb, "NTFS", 0);
        Assert.Null(msg);
    }

    [Fact]
    public void ZeroTotalBytes_ReturnsNull()
    {
        // Guard against DriveInfo returning 0 for a Docker overlay / bind mount.
        var msg = StatusQueryService.ComputeLowSpaceWarning(0, 0, "overlay", 90);
        Assert.Null(msg);
    }
}
