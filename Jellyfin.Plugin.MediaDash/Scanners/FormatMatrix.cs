using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// Single source of truth for which extensions Jellyfin can render, and what to convert them to
/// when it can't. Read by the FormatCompatibilityScanner and by the Phase 2+ converters.
/// Data only — the only code here is the ext-to-row lookup.
/// </summary>
public static class FormatMatrix
{
    /// <summary>Every row in the matrix. Order irrelevant.</summary>
    public static readonly IReadOnlyList<FormatEntry> All = new FormatEntry[]
    {
        // Ebooks — Jellyfin's browser reader handles EPUB + PDF only.
        new(".epub", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: true,  TargetExtension: null,    ConverterKey: "none",  UserAdvice: string.Empty),
        new(".pdf",  MediaKind.Ebook, ServerIndexes: true,  ClientRenders: true,  TargetExtension: null,    ConverterKey: "none",  UserAdvice: string.Empty),
        new(".mobi", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert MOBI to EPUB so Jellyfin's browser reader can display it."),
        new(".azw",  MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert AZW to EPUB so Jellyfin's browser reader can display it."),
        new(".azw3", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert AZW3 to EPUB so Jellyfin's browser reader can display it."),
        new(".djvu", MediaKind.Ebook, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".pdf",  ConverterKey: "ebook", UserAdvice: "Convert DjVu to PDF so Jellyfin's browser reader can display it."),
        new(".txt",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert plain text to EPUB so Jellyfin indexes and displays it."),
        new(".rtf",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert RTF to EPUB so Jellyfin indexes and displays it."),
        new(".doc",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert DOC to EPUB so Jellyfin indexes and displays it."),
        new(".docx", MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert DOCX to EPUB so Jellyfin indexes and displays it."),
        new(".fb2",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert FB2 to EPUB so Jellyfin indexes and displays it."),
        new(".lit",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert LIT to EPUB so Jellyfin indexes and displays it."),
        new(".lrf",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert LRF to EPUB so Jellyfin indexes and displays it."),
        new(".pdb",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert PDB to EPUB so Jellyfin indexes and displays it."),
        new(".htmlz", MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert HTMLZ to EPUB so Jellyfin indexes and displays it."),
        new(".chm",  MediaKind.Ebook, ServerIndexes: false, ClientRenders: false, TargetExtension: ".epub", ConverterKey: "ebook", UserAdvice: "Convert CHM to EPUB so Jellyfin indexes and displays it."),

        // Comics.
        new(".cbz", MediaKind.Comic, ServerIndexes: true,  ClientRenders: true,  TargetExtension: null,    ConverterKey: "none",  UserAdvice: string.Empty),
        new(".cbr", MediaKind.Comic, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".cbz",  ConverterKey: "comic", UserAdvice: "Repack CBR as CBZ so every Jellyfin client can display it."),
        new(".cb7", MediaKind.Comic, ServerIndexes: true,  ClientRenders: false, TargetExtension: ".cbz",  ConverterKey: "comic", UserAdvice: "Repack CB7 as CBZ so every Jellyfin client can display it."),

        // Audiobooks — DRM formats detected but never converted.
        new(".m4b", MediaKind.Audiobook, ServerIndexes: true, ClientRenders: true,  TargetExtension: null, ConverterKey: "none", UserAdvice: string.Empty),
        new(".aax", MediaKind.Audiobook, ServerIndexes: true, ClientRenders: false, TargetExtension: null, ConverterKey: "none", UserAdvice: "AAX is Audible-DRM encrypted. Convert it with Audible or a DRM-removal tool of your choice, then re-import the M4B."),
        new(".aa",  MediaKind.Audiobook, ServerIndexes: true, ClientRenders: false, TargetExtension: null, ConverterKey: "none", UserAdvice: "AA is Audible-DRM encrypted. Convert it with Audible or a DRM-removal tool of your choice, then re-import the M4B."),

        // Images — the browser client renders JPG/PNG/GIF/WebP but not HEIC/TIFF/BMP/RAW.
        new(".heic", MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert HEIC to JPG for browser display."),
        new(".heif", MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert HEIF to JPG for browser display."),
        new(".tif",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert TIFF to JPG for browser display."),
        new(".tiff", MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert TIFF to JPG for browser display."),
        new(".bmp",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert BMP to JPG for browser display."),
        new(".raw",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".nef",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert Nikon RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".cr2",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert Canon RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".arw",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert Sony RAW to JPG for browser display (original preserved in recycle bin)."),
        new(".dng",  MediaKind.Image, ServerIndexes: true, ClientRenders: false, TargetExtension: ".jpg", ConverterKey: "image", UserAdvice: "Convert DNG to JPG for browser display (original preserved in recycle bin)."),

        // Music — obscure tracker formats and legacy lossless.
        new(".669", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".it",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".mod", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".s3m", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".xm",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Render the tracker file to FLAC so Jellyfin clients can play it."),
        new(".ape", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack Monkey's Audio as FLAC for broader client support (lossless)."),
        new(".wv",  MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack WavPack as FLAC for broader client support (lossless)."),
        new(".tak", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack TAK as FLAC for broader client support (lossless)."),
        new(".tta", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".flac", ConverterKey: "music", UserAdvice: "Repack TTA as FLAC for broader client support (lossless)."),
        new(".wma", MediaKind.Music, ServerIndexes: true, ClientRenders: false, TargetExtension: ".mp3",  ConverterKey: "music", UserAdvice: "Convert WMA to MP3 for broader client support."),

        // Video — one entry. Codec-level video problems are handled by PlayabilityScanner.
        new(".rmvb", MediaKind.Video, ServerIndexes: true, ClientRenders: false, TargetExtension: ".mkv", ConverterKey: "video", UserAdvice: "Repackage RMVB into MKV so Jellyfin's transcoder can handle it."),
    };

    private static readonly Dictionary<string, FormatEntry> ByExt =
        All.ToDictionary(r => r.Extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the row for a file's extension, or null when the extension isn't in the matrix.</summary>
    /// <param name="path">Any string ending in an extension; only the extension is inspected.</param>
    /// <returns>The matching <see cref="FormatEntry"/>, or null when the extension is unknown.</returns>
    public static FormatEntry? Classify(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return null;
        }

        return ByExt.TryGetValue(ext, out var row) ? row : null;
    }
}
