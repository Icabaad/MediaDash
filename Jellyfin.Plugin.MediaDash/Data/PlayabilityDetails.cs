namespace Jellyfin.Plugin.MediaDash.Data;

/// <summary>
/// Typed view of the <c>DetailsJson</c> payload written by
/// <see cref="Jellyfin.Plugin.MediaDash.Scanners.PlayabilityScanner"/>. Persisted keys
/// (<c>reason</c>, <c>rename_to_ext</c>) are preserved verbatim — see
/// <see cref="IssueDetailsReader.TryReadPlayability"/>. Optional absent fields stay null;
/// wrong-type / non-object roots reject the payload entirely.
/// </summary>
/// <param name="Reason">The scanner reason tag (e.g. <c>drm-protected</c>,
/// <c>container-extension-mismatch</c>, <c>no-audio</c>) — matches the internal
/// dispatch keys in <c>PlayabilityFixer</c>.</param>
/// <param name="RenameToExtension">Target extension for the rename branch. Never null-
/// but-inferrable: an absent value on a mismatch payload means the fixer must refuse
/// rather than guess a destructive rename destination.</param>
internal sealed record PlayabilityDetails(string? Reason, string? RenameToExtension);
