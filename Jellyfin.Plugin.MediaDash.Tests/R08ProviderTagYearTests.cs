using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

/// <summary>
/// Root-cause audit 2026-09-19 R08: "Resolve <c>Doctor Who (1963) [tvdbid-76107]</c> with
/// metadata year 2005. Production resolver returns <c>Doctor Who (2005)</c>. Untagged
/// <c>Doctor Who (1963)</c> correctly retains 1963." The year regex assumed the year ends the
/// folder name; trailing bracketed provider tags broke the on-disk rule → fallthrough to
/// Jellyfin's collapsed reboot metadata year. Fix: tolerate optional trailing
/// <c>[provider-id]</c> tags in <see cref="MediaGrouperScanner.ResolveSafeSeriesName"/>.
/// </summary>
public sealed class R08ProviderTagYearTests
{
    private const string TvRoot = "/media/tv";

    [Fact]
    public void ProviderTagAfterYear_TvdbId_KeepsOnDiskYear()
    {
        // The exact audit fixture.
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 2005, // Jellyfin's TVDb match wrongly collapsed to the reboot year
            episodeFilePath: "/media/tv/Doctor Who (1963) [tvdbid-76107]/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (1963) [tvdbid-76107]", result);
    }

    [Fact]
    public void ProviderTagAfterYear_TmdbId_KeepsOnDiskYear()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 2005,
            episodeFilePath: "/media/tv/Doctor Who (2005) [tmdbid-57243]/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (2005) [tmdbid-57243]", result);
    }

    [Fact]
    public void ProviderTagAfterYear_ImdbId_KeepsOnDiskYear()
    {
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Silo",
            seriesPremiereYear: 2023,
            episodeFilePath: "/media/tv/Silo (2023) [imdbid-tt14688458]/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Silo (2023) [imdbid-tt14688458]", result);
    }

    [Fact]
    public void ProviderTagAfterYear_MultipleTags_KeepsOnDiskYear()
    {
        // Some users tag both TVDb + IMDb; both must be tolerated.
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 2005,
            episodeFilePath: "/media/tv/Doctor Who (1963) [tvdbid-76107] [imdbid-tt0056751]/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (1963) [tvdbid-76107] [imdbid-tt0056751]", result);
    }

    [Fact]
    public void PlainYearOnly_StillWorks_RegressionGuard()
    {
        // The pre-fix behavior must be preserved: plain "Series (YYYY)" without any trailing
        // tag still matches. Guards against the regex extension accidentally requiring brackets.
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 2005,
            episodeFilePath: "/media/tv/Doctor Who (1963)/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (1963)", result);
    }

    [Fact]
    public void MetadataFallback_RejectsBracketedTagWithoutYear()
    {
        // Defence: `[tvdbid-76107]` alone (no leading year) must NOT be treated as on-disk
        // disambiguation. Falls through to Rule 2 → SeriesName + PremiereDate year.
        var result = MediaGrouperScanner.ResolveSafeSeriesName(
            episodeSeriesName: "Doctor Who",
            seriesPremiereYear: 2005,
            episodeFilePath: "/media/tv/Doctor Who [tvdbid-76107]/Season 01/E01.mkv",
            tvRoot: TvRoot);
        Assert.Equal("Doctor Who (2005)", result);
    }
}
