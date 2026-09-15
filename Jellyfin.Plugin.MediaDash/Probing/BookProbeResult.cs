namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>Result of an integrity check against a book file.</summary>
/// <param name="Ok">True when the container parsed cleanly.</param>
/// <param name="Reason">Human-readable reason when the check failed, else null.
/// Certain reason strings are tagged with a prefix so the scanner can route to a
/// non-repair path: <c>drm:</c> = the file is encrypted and no repair can help;
/// <c>mismatch:&lt;target-ext&gt;:</c> = extension disagrees with the container
/// magic and the file can be safely renamed. Everything else = ordinary corruption.</param>
public readonly record struct BookProbeResult(bool Ok, string? Reason);
