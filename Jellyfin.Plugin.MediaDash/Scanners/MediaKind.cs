namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// The kind of media a file is, for the format-compatibility feature. Different from Jellyfin's
/// BaseItemKind because it's coarser (Ebook vs. Comic is one distinction Jellyfin doesn't draw)
/// and includes synthetic entries (AudiobookFolder — a folder of chaptered mp3s) that Phase 3 adds.
/// </summary>
public enum MediaKind
{
    /// <summary>A video file (movie, episode, etc.).</summary>
    Video,

    /// <summary>A generic audio file.</summary>
    Audio,

    /// <summary>An audiobook file or chaptered audio container.</summary>
    Audiobook,

    /// <summary>An ebook in any format (EPUB, PDF, MOBI, etc.).</summary>
    Ebook,

    /// <summary>A comic book archive (CBZ, CBR, etc.).</summary>
    Comic,

    /// <summary>An image file (HEIC, TIFF, RAW camera formats, etc.).</summary>
    Image,

    /// <summary>A music file, including tracker modules and lossless formats.</summary>
    Music,
}
