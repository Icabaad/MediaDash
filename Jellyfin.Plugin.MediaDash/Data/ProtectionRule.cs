using System;

namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// One "Never modify" rule against a folder path or Jellyfin library. Applied by
/// <see cref="Fixers.ProtectionService"/> at both scan-emit and fix-execute time.
/// </summary>
public sealed class ProtectionRule
{
    /// <summary>Gets the row id (0 for a fresh, unsaved rule).</summary>
    public long Id { get; init; }

    /// <summary>Gets the rule kind: <c>"folder"</c> or <c>"library"</c>.</summary>
    public string Kind { get; init; } = "folder";

    /// <summary>Gets the target: absolute folder path (folder kind) or Jellyfin library item id (library kind).</summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>
    /// Gets the scanners blocked at <see cref="Target"/>. Value is <c>"*"</c> (block every scanner),
    /// an empty string (block none — protection only affects fixers), or a comma-separated list of
    /// <see cref="IssueType"/> names (e.g. <c>"Quality,Duplicate"</c>).
    /// </summary>
    public string ScannersMask { get; init; } = string.Empty;

    /// <summary>Gets the fixers blocked at <see cref="Target"/>. Same shape as <see cref="ScannersMask"/>.</summary>
    public string FixersMask { get; init; } = string.Empty;

    /// <summary>Gets the optional user note explaining why this rule exists.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>Gets when the rule was created.</summary>
    public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
}
