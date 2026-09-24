using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Language code matching that copes with ISO 639-2 bibliographic/terminological variants (fre/fra, ger/deu).
/// </summary>
public static class LanguageHelper
{
    private static readonly Dictionary<string, string> BibliographicToTerminological = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alb"] = "sqi",
        ["arm"] = "hye",
        ["baq"] = "eus",
        ["bur"] = "mya",
        ["chi"] = "zho",
        ["cze"] = "ces",
        ["dut"] = "nld",
        ["fre"] = "fra",
        ["geo"] = "kat",
        ["ger"] = "deu",
        ["gre"] = "ell",
        ["ice"] = "isl",
        ["mac"] = "mkd",
        ["mao"] = "mri",
        ["may"] = "msa",
        ["per"] = "fas",
        ["rum"] = "ron",
        ["slo"] = "slk",
        ["tib"] = "bod",
        ["wel"] = "cym"
    };

    // Macrolanguage equivalence groups. A user who allows the macrolanguage code (Jellyfin's
    // picker labels these plainly as "Chinese", "Arabic", "Norwegian", etc.) should have tracks
    // tagged with any individual variant kept too — most real-world media is tagged with the
    // specific individual, so a macro-only allowed list would silently delete everything.
    // Every code in a group matches every other code in the same group inside IsAllowed().
    //
    // Design tradeoff: a user who allows a specific individual (e.g. "cmn" Mandarin only) will
    // ALSO keep sibling individuals (e.g. "yue" Cantonese) with this shape. Deliberate: the
    // silent-drop failure mode is worse than the keep-extra one, and matches the Norwegian
    // precedent that shipped in 1.0.7.6. If a user files a strict-individual-filter request we
    // can layer a hierarchical variant on top; until then flat equivalence is enough.
    //
    // Codes here are the TERMINOLOGICAL form because `Normalize` runs
    // `BibliographicToTerminological` first — "chi" arrives as "zho", "may" as "msa", "per" as
    // "fas", so bib aliases don't need to appear in the group.
    private static readonly IReadOnlyList<HashSet<string>> EquivalenceGroups =
    [
        // Norwegian: macrolanguage "nor" spans Bokmål "nob" and Nynorsk "nno". Norwegian media
        // is near-universally tagged with the specific variant, so a "nor"-only allowed list
        // dropped both. Shipped in 1.0.7.6; kept for regression coverage.
        new(StringComparer.OrdinalIgnoreCase) { "nor", "nob", "nno" },

        // Chinese: macrolanguage "zho" spans Mandarin "cmn", Cantonese "yue", plus the six other
        // Chinese-family individuals ISO 639-3 recognises (Wu, Min Nan, Hakka, Xiang, Gan). Real
        // user files — anime dubs, HK/TW/CN films, mainland releases — routinely tag audio as
        // "cmn" or "yue"; a "zho" allowed list (which is what Jellyfin's "Chinese" picker
        // produces) would drop every one of them silently.
        new(StringComparer.OrdinalIgnoreCase) { "zho", "cmn", "yue", "wuu", "nan", "hak", "hsn", "gan" },

        // Arabic: macrolanguage "ara" spans Modern Standard "arb" (Netflix / newer Amazon rips
        // often tag this), Egyptian "arz", Moroccan "ary", Levantine "apc", Algerian "arq",
        // Tunisian "aeb", Iraqi "acm", Sudanese "apd". Regional dubs on Middle Eastern releases
        // are near-universally tagged with the individual code, not "ara".
        new(StringComparer.OrdinalIgnoreCase) { "ara", "arb", "arz", "ary", "apc", "arq", "aeb", "acm", "apd" },

        // Portuguese: "pob" is not ISO 639-2 — there's no bibliographic code for pt-BR — but
        // scene groups and some Plex/Emby tooling use it for Brazilian Portuguese in stream
        // metadata. Filename tags like "pt-BR" survive the region-strip inside Normalize, but
        // bare "pob" in a stream metadata field does not fall out anywhere else — needs the
        // explicit pair here to be treated as Portuguese.
        new(StringComparer.OrdinalIgnoreCase) { "por", "pob" },

        // Malay: macrolanguage "msa" spans Standard Malay "zsm". Southeast Asian releases tag
        // as "zsm" almost exclusively.
        new(StringComparer.OrdinalIgnoreCase) { "msa", "zsm" },

        // Persian: macrolanguage "fas" spans Iranian Persian "pes" and Dari "prs". Iranian
        // releases are usually "pes"; Afghan releases "prs".
        new(StringComparer.OrdinalIgnoreCase) { "fas", "pes", "prs" }
    ];

    /// <summary>
    /// Normalizes a language tag to a lowercase ISO 639-2/T code. Null, empty and unknown map to "und".
    /// </summary>
    /// <param name="language">The raw language tag from the media file.</param>
    /// <returns>The normalized code.</returns>
    public static string Normalize(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return "und";
        }

        var lang = language.Trim().ToLowerInvariant();

        var regionSeparator = lang.IndexOfAny(['-', '_']);
        if (regionSeparator >= 0)
        {
            lang = lang[..regionSeparator];
        }

        // ISO 639-1 (2-letter) → 639-2/T (3-letter) via CultureInfo. Without this, an "en"-tagged track
        // never matches an allowed list of ["eng"] and gets flagged for removal — including the file's
        // only English audio track when Spanish also exists ("last audio" invariant wouldn't save it).
        if (lang.Length == 2)
        {
            try
            {
                var culture = System.Globalization.CultureInfo.GetCultureInfo(lang);
                if (!string.IsNullOrEmpty(culture.ThreeLetterISOLanguageName) && culture.ThreeLetterISOLanguageName != "xx")
                {
                    lang = culture.ThreeLetterISOLanguageName.ToLowerInvariant();
                }
            }
            catch (System.Globalization.CultureNotFoundException)
            {
            }
        }

        return BibliographicToTerminological.TryGetValue(lang, out var terminological) ? terminological : lang;
    }

    /// <summary>
    /// Checks whether a track language is in the allowed list. Undetermined ("und", missing) tracks are always allowed,
    /// because deleting a track whose language is unknown is not safe.
    /// </summary>
    /// <param name="language">The raw language tag from the media file.</param>
    /// <param name="allowed">The allowed ISO 639-2 codes.</param>
    /// <returns>True when the track should be kept.</returns>
    public static bool IsAllowed(string? language, IReadOnlyList<string> allowed)
    {
        var normalized = Normalize(language);
        if (normalized == "und")
        {
            return true;
        }

        foreach (var entry in allowed)
        {
            var allowedNorm = Normalize(entry);
            if (string.Equals(allowedNorm, normalized, StringComparison.Ordinal))
            {
                return true;
            }

            if (SameEquivalenceGroup(allowedNorm, normalized))
            {
                return true;
            }
        }

        return false;
    }

    private static bool SameEquivalenceGroup(string a, string b)
    {
        foreach (var group in EquivalenceGroups)
        {
            if (group.Contains(a) && group.Contains(b))
            {
                return true;
            }
        }

        return false;
    }
}
