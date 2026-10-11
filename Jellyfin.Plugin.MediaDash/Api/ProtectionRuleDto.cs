using System;

namespace Jellyfin.Plugin.MediaDash.Api;

/// <summary>Wire shape for <c>/MediaDash/Protection</c>. Roadmap i-110-protected-media.</summary>
public sealed class ProtectionRuleDto
{
    /// <summary>Gets or sets the row id (0 on POST).</summary>
    public long Id { get; set; }

    /// <summary>Gets or sets the rule kind: <c>folder</c> or <c>library</c>.</summary>
    public string Kind { get; set; } = "folder";

    /// <summary>Gets or sets the target: absolute folder path (folder kind) or library id (library kind).</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Gets or sets which scanners are blocked: <c>*</c> = all, empty = none, else comma-separated IssueType names.</summary>
    public string ScannersMask { get; set; } = string.Empty;

    /// <summary>Gets or sets which fixers are blocked. Same shape as <see cref="ScannersMask"/>.</summary>
    public string FixersMask { get; set; } = string.Empty;

    /// <summary>Gets or sets the optional user note.</summary>
    public string Note { get; set; } = string.Empty;

    /// <summary>Gets or sets when the rule was created.</summary>
    public DateTime CreatedAtUtc { get; set; }
}
