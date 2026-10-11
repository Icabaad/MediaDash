using System.Reflection;
using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class MissingSubtitleLanguageMatchingTests
{
    [Theory]
    [InlineData("nob", "nor", true)]
    [InlineData("nno", "nor", true)]
    [InlineData("cmn", "zho", true)]
    [InlineData("yue", "chi", true)]
    [InlineData("arb", "ara", true)]
    [InlineData("sv-SE", "swe", true)]
    [InlineData("sw", "swe", false)]
    [InlineData("swe", "nor", false)]
    [InlineData(null, "eng", false)]
    [InlineData("", "eng", false)]
    [InlineData("und", "eng", false)]
    public void PresenceMatchesEquivalentLanguagesButNotUnknownOrUnrelated(string? track, string wanted, bool expected)
    {
        var method = typeof(MissingSubtitleScanner).GetMethod("HasAnyMatch", BindingFlags.NonPublic | BindingFlags.Static)!;
        var actual = (bool)method.Invoke(null, new object?[] { track, new[] { wanted } })!;
        Assert.Equal(expected, actual);
    }
}
