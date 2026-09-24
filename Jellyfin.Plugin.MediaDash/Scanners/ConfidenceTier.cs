namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>Confidence tier for a duplicate pair. See the rework spec §2.</summary>
internal enum ConfidenceTier
{
    /// <summary>Byte-identical (size + full hash match). Confidence 1.00.</summary>
    Exact,

    /// <summary>Grouped by a shared provider ID (Tmdb/Imdb/Tvdb/Isbn/MusicBrainzTrack). Base 0.90.</summary>
    Identified,

    /// <summary>Grouped by a fallback key (movie-name/episode/book-name/audio-name). Base 0.70.</summary>
    Heuristic
}
