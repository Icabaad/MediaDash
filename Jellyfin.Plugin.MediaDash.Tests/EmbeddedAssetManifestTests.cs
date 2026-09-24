using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.MediaDash;
using Jellyfin.Plugin.MediaDash.Api;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Manifest sanity for the 06-D physical asset split. Guards the two silent breakages
/// this refactor can produce: (1) whitelist entry pointing at a resource that isn't
/// actually embedded (a wrong path in csproj or a rename that missed the whitelist);
/// (2) duplicate whitelist entries (would work but signal a merge slip).
/// </summary>
public sealed class EmbeddedAssetManifestTests
{
    [Fact]
    public void EveryWhitelistedAssetResolvesToAnEmbeddedResource()
    {
        var assembly = typeof(Plugin).Assembly;
        var embedded = new HashSet<string>(assembly.GetManifestResourceNames());

        foreach (var name in PluginAssetController.WhitelistNames)
        {
            var descriptor = PluginAssetController.Lookup(name);
            Assert.NotNull(descriptor);

            var expected = $"Jellyfin.Plugin.MediaDash.Configuration.{descriptor!.EmbeddedSuffix}";
            Assert.True(
                embedded.Contains(expected),
                $"Whitelisted asset '{name}' points at embedded resource '{expected}' but the assembly does not contain it. " +
                $"Assembly resources present: {string.Join(", ", embedded.OrderBy(x => x))}");
        }
    }

    [Fact]
    public void WhitelistHasNoDuplicateKeys()
    {
        var names = PluginAssetController.WhitelistNames.ToList();
        var distinct = names.Distinct().Count();
        Assert.Equal(distinct, names.Count);
    }

    [Fact]
    public void WhitelistIsNotEmpty()
    {
        // Guards against a future "clean up unused entries" pass accidentally wiping the
        // whitelist and letting the whole feature silently no-op.
        Assert.NotEmpty(PluginAssetController.WhitelistNames);
    }
}
