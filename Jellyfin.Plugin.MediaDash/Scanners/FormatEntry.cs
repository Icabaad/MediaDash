namespace Jellyfin.Plugin.MediaDash.Scanners;

/// <summary>
/// One row in the format-compatibility matrix. See
/// docs/superpowers/specs/2026-09-28-format-compatibility-conversion-design.md §4.
/// </summary>
/// <param name="Extension">Lower-case extension including the dot (e.g. ".mobi").</param>
/// <param name="Kind">Media kind this extension classifies as.</param>
/// <param name="ServerIndexes">True when Jellyfin's NamingOptions include this extension.</param>
/// <param name="ClientRenders">True when Jellyfin's browser client can display the file.</param>
/// <param name="TargetExtension">Extension a converter should produce, or null to detect-only.</param>
/// <param name="ConverterKey">Registry key of the converter that handles this row; "none" when no conversion.</param>
/// <param name="UserAdvice">One-line SuggestedFix text shown on the Issues tab.</param>
public sealed record FormatEntry(
    string Extension,
    MediaKind Kind,
    bool ServerIndexes,
    bool ClientRenders,
    string? TargetExtension,
    string ConverterKey,
    string UserAdvice);
