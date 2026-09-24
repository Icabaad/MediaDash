using Jellyfin.Plugin.MediaDash.Configuration;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Snapshot invariants for <see cref="PluginConfigurationProvider"/>. The dashboard, scan and fix
/// paths must never see a torn config or a shared array reference — a save mid-run must NOT
/// mutate an already-taken snapshot, and mutating a taken snapshot must NOT alter saved config.
/// </summary>
public class ConfigurationSnapshotTests
{
    [Fact]
    public void GetSnapshot_ReturnsFreshArrayEachTime()
    {
        // Verbatim from the plan (§02-A snapshot-copy example). Two consecutive snapshots must
        // hold independent array instances even when the underlying source did not change.
        var live = new PluginConfiguration
        {
            AllowedAudioLanguages = new[] { "eng", "jpn" }
        };
        var provider = new PluginConfigurationProvider(() => live);

        var first = provider.GetSnapshot();
        var second = provider.GetSnapshot();

        Assert.NotSame(first.AllowedAudioLanguages, second.AllowedAudioLanguages);
        first.AllowedAudioLanguages[0] = "changed-in-test";
        Assert.NotEqual("changed-in-test", provider.GetSnapshot().AllowedAudioLanguages[0]);
    }

    [Fact]
    public void MutatingSnapshot_DoesNotAlterSavedConfig()
    {
        var live = new PluginConfiguration
        {
            EnabledLibraries = new[] { "lib-a" },
            FixerDisabled = new[] { "Duplicate" }
        };
        var provider = new PluginConfigurationProvider(() => live);

        var snap = provider.GetSnapshot();
        snap.EnabledLibraries[0] = "lib-b";
        snap.FixerDisabled[0] = "Playability";
        snap.DryRun = !snap.DryRun;
        snap.MaxResolutionHeight = 2160;

        Assert.Equal("lib-a", live.EnabledLibraries[0]);
        Assert.Equal("Duplicate", live.FixerDisabled[0]);
        Assert.NotEqual(snap.DryRun, live.DryRun);
        Assert.NotEqual(2160, live.MaxResolutionHeight);
    }

    [Fact]
    public void NextSnapshot_SeesChangedSettings()
    {
        // Simulates a save between two calls: the second snapshot must observe the new value.
        var live = new PluginConfiguration();
        live.MaxResolutionHeight = 1080;
        var provider = new PluginConfigurationProvider(() => live);

        var beforeSave = provider.GetSnapshot();
        Assert.Equal(1080, beforeSave.MaxResolutionHeight);

        live.MaxResolutionHeight = 720;
        var afterSave = provider.GetSnapshot();
        Assert.Equal(720, afterSave.MaxResolutionHeight);
        // Prior snapshot is unaffected.
        Assert.Equal(1080, beforeSave.MaxResolutionHeight);
    }

    [Fact]
    public void SnapshotPreservesXmlDefaults()
    {
        // Do NOT use JSON serialisation to clone (would misread null-defaults on XML-serialised
        // properties). A blank source snapshot must land the constructor defaults, not an
        // unspecified nulled/zeroed shape.
        var live = new PluginConfiguration();
        var provider = new PluginConfigurationProvider(() => live);

        var snap = provider.GetSnapshot();

        Assert.Equal(live.MaxResolutionHeight, snap.MaxResolutionHeight);
        Assert.Equal(live.PreferredCodec, snap.PreferredCodec);
        Assert.Equal(live.RecycleBinRetentionDays, snap.RecycleBinRetentionDays);
        Assert.Equal(live.KeeperPolicyOrder, snap.KeeperPolicyOrder);
        Assert.True(snap.DryRun); // constructor default
    }
}
