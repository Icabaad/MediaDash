using Jellyfin.Plugin.MediaDash.Fixers;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Boundary-parser coverage for the provider-quota substring detection. Before 01-B the
/// scheduler had its own <c>FixTask.IsSubtitleProviderQuotaExhausted(message)</c> matcher;
/// that classifier is gone and the responsibility moved to <see cref="MissingSubtitleFixer.LooksLikeProviderQuota"/>
/// — the one place inside the plugin that legitimately parses OpenSubtitles' wording (per the
/// master plan's "third-party stderr/provider strings may be parsed once at that integration
/// boundary" rule). The scheduler now branches on <c>result.Reason == FixReason.ProviderQuota</c>.
/// </summary>
public class FixTaskSubtitleQuotaTests
{
    [Theory]
    [InlineData("OpenSubtitles download limit reached")]
    [InlineData("en: OpenSubtitles download limit reached")]
    [InlineData("en: OpenSubtitles download limit reached; fr: OpenSubtitles download limit reached")]
    [InlineData("download quota exceeded, try again later")]
    public void LooksLikeProviderQuota_MatchesKnownWordings(string message)
    {
        Assert.True(MissingSubtitleFixer.LooksLikeProviderQuota(message));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("en: no matches from any provider")]
    [InlineData("en: provider unreachable (Name or service not known)")]
    [InlineData("Jellyfin can't write to '/mnt/media'.")]
    public void LooksLikeProviderQuota_LeavesUnrelatedFailuresAlone(string? message)
    {
        Assert.False(MissingSubtitleFixer.LooksLikeProviderQuota(message));
    }
}
