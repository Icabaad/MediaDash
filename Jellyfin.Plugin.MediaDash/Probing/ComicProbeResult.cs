namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Result of an integrity check against a comic file.
/// </summary>
/// <param name="Ok">True when the archive parsed cleanly and contained at least one image entry.</param>
/// <param name="Reason">Human-readable reason when the check failed, else null. See
/// <see cref="BookProbeResult"/> for the reason-prefix routing convention shared with
/// book probes.</param>
public readonly record struct ComicProbeResult(bool Ok, string? Reason);
