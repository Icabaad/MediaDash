namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>
/// The subset of <see cref="Data.IssueType"/> that is safe to create manually from the file
/// browser context menu. Intentionally narrower than the full enum — most types need scanner-only
/// metadata and cannot be constructed from a path alone.
/// </summary>
public enum ManualIssueType
{
    /// <summary>Re-encode to the configured quality ceiling.</summary>
    Quality = 0,

    /// <summary>Convert to a client-renderable format via the FormatMatrix.</summary>
    FormatCompatibility = 1
}

/// <summary>
/// Payload for POST MediaDash/Issues/Manual.
/// </summary>
public sealed class ManualIssueRequest
{
    /// <summary>Gets or sets the absolute path of the file the user right-clicked.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the issue type the user picked from the dropdown.</summary>
    public ManualIssueType Type { get; set; }
}
