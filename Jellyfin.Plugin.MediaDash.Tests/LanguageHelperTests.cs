using Jellyfin.Plugin.MediaDash.Scanners;
using Xunit;

namespace Jellyfin.Plugin.MediaDash.Tests;

public class LanguageHelperTests
{
    [Theory]
    [InlineData(null, "und")]
    [InlineData("", "und")]
    [InlineData("  ", "und")]
    [InlineData("eng", "eng")]
    [InlineData("ENG", "eng")]
    [InlineData("fre", "fra")]
    [InlineData("ger", "deu")]
    [InlineData("chi", "zho")]
    [InlineData("dut", "nld")]
    [InlineData("es-MX", "spa")]
    [InlineData("ES_mx", "spa")]
    [InlineData("en-US", "eng")]
    public void Normalize_MapsBibliographicVariantsAndCase(string? input, string expected)
    {
        Assert.Equal(expected, LanguageHelper.Normalize(input));
    }

    [Fact]
    public void IsAllowed_UndeterminedIsAlwaysAllowed()
    {
        Assert.True(LanguageHelper.IsAllowed(null, ["eng"]));
        Assert.True(LanguageHelper.IsAllowed("und", ["eng"]));
        Assert.True(LanguageHelper.IsAllowed("", ["eng"]));
    }

    [Fact]
    public void IsAllowed_MatchesAcrossIsoVariants()
    {
        // Track tagged bibliographic, config uses terminological — and vice versa.
        Assert.True(LanguageHelper.IsAllowed("fre", ["fra"]));
        Assert.True(LanguageHelper.IsAllowed("fra", ["fre"]));
        Assert.True(LanguageHelper.IsAllowed("deu", ["ger"]));
    }

    [Fact]
    public void IsAllowed_RejectsLanguagesOutsideList()
    {
        Assert.False(LanguageHelper.IsAllowed("fra", ["eng"]));
        Assert.False(LanguageHelper.IsAllowed("jpn", ["eng", "spa"]));
    }

    [Theory]
    // Norwegian macrolanguage: a user with "nor" allowed should keep Bokmål (nob) and Nynorsk (nno)
    // tracks — most Norwegian media is tagged with the specific variant. Symmetric: a user with
    // "nob" or "nno" allowed should also keep generic "nor" tracks.
    [InlineData("nob", new[] { "nor" })]
    [InlineData("nno", new[] { "nor" })]
    [InlineData("nor", new[] { "nob" })]
    [InlineData("nor", new[] { "nno" })]
    [InlineData("nob", new[] { "nno" })]
    public void IsAllowed_TreatsNorwegianVariantsAsEquivalent(string track, string[] allowed)
    {
        Assert.True(LanguageHelper.IsAllowed(track, allowed));
    }

    [Fact]
    public void IsAllowed_NorwegianDoesNotBleedIntoUnrelatedLanguages()
    {
        // Sanity: equivalence group is scoped. "nor" must not match unrelated codes.
        Assert.False(LanguageHelper.IsAllowed("swe", ["nor"]));
        Assert.False(LanguageHelper.IsAllowed("dan", ["nob"]));
    }

    [Theory]
    // Chinese macrolanguage: Jellyfin's "Chinese" picker produces "zho" (via the "chi" → "zho"
    // bib/term mapping). Real anime dubs, HK/TW/CN films, and mainland releases tag tracks with
    // the specific individual code — cmn (Mandarin), yue (Cantonese), wuu (Wu), etc. — so a
    // "zho"-only allowed list silently dropped every one before this group landed.
    [InlineData("cmn", new[] { "zho" })]
    [InlineData("yue", new[] { "zho" })]
    [InlineData("wuu", new[] { "zho" })]
    [InlineData("nan", new[] { "zho" })]
    [InlineData("hak", new[] { "zho" })]
    // Symmetric: user allows an individual, macro-tagged track survives.
    [InlineData("zho", new[] { "cmn" })]
    [InlineData("zho", new[] { "yue" })]
    // "chi" (bibliographic) allowed list is same as "zho" because Normalize maps it.
    [InlineData("cmn", new[] { "chi" })]
    public void IsAllowed_TreatsChineseVariantsAsEquivalent(string track, string[] allowed)
    {
        Assert.True(LanguageHelper.IsAllowed(track, allowed));
    }

    [Theory]
    // Arabic macrolanguage: Netflix / Amazon rips tag audio "arb" (Modern Standard); regional
    // dubs use "arz" (Egyptian), "ary" (Moroccan), "apc" (Levantine). "ara"-only allowed list
    // silently dropped every one.
    [InlineData("arb", new[] { "ara" })]
    [InlineData("arz", new[] { "ara" })]
    [InlineData("ary", new[] { "ara" })]
    [InlineData("apc", new[] { "ara" })]
    [InlineData("ara", new[] { "arb" })]
    public void IsAllowed_TreatsArabicVariantsAsEquivalent(string track, string[] allowed)
    {
        Assert.True(LanguageHelper.IsAllowed(track, allowed));
    }

    [Theory]
    // Portuguese: "pob" is a non-ISO tag scene groups and some Plex/Emby tooling use for
    // Brazilian Portuguese in stream metadata. "pt-BR" in filenames survives region-strip, but
    // bare "pob" needs the explicit equivalence.
    [InlineData("pob", new[] { "por" })]
    [InlineData("por", new[] { "pob" })]
    // Belt-and-braces: pt-BR still normalises to por via region-strip + CultureInfo.
    [InlineData("pt-BR", new[] { "por" })]
    public void IsAllowed_TreatsBrazilianPortugueseAsPortuguese(string track, string[] allowed)
    {
        Assert.True(LanguageHelper.IsAllowed(track, allowed));
    }

    [Theory]
    // Malay macrolanguage ("msa" / bib "may") ↔ Standard Malay "zsm".
    [InlineData("zsm", new[] { "msa" })]
    [InlineData("msa", new[] { "zsm" })]
    // Persian macrolanguage ("fas" / bib "per") ↔ Iranian Persian "pes" and Dari "prs".
    [InlineData("pes", new[] { "fas" })]
    [InlineData("prs", new[] { "fas" })]
    [InlineData("fas", new[] { "pes" })]
    public void IsAllowed_TreatsMalayAndPersianVariantsAsEquivalent(string track, string[] allowed)
    {
        Assert.True(LanguageHelper.IsAllowed(track, allowed));
    }

    [Fact]
    public void IsAllowed_MacrolanguageGroupsAreScoped()
    {
        // Sanity across all new groups: equivalences don't cross into unrelated languages.
        Assert.False(LanguageHelper.IsAllowed("cmn", ["ara"]));  // Chinese ≠ Arabic
        Assert.False(LanguageHelper.IsAllowed("pob", ["fas"]));  // Portuguese ≠ Persian
        Assert.False(LanguageHelper.IsAllowed("zsm", ["zho"]));  // Malay ≠ Chinese
        Assert.False(LanguageHelper.IsAllowed("arb", ["eng"]));  // Arabic ≠ English
    }
}
