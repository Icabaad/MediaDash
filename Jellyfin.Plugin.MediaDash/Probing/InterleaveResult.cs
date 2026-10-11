namespace Jellyfin.Plugin.MediaDash.Probing;

/// <summary>
/// Three-way outcome of an interleave check. Collapsing <see cref="Indeterminate"/> into
/// <see cref="Clean"/> would let a probe failure (missing ffprobe, timeout, process crash) pass as
/// "file is fine" — critical for post-repair verification where accepting an indeterminate result
/// as success could promote a bad replacement over the user's original.
/// </summary>
public enum InterleaveStatus
{
    /// <summary>The file was walked successfully and audio sits next to its video.</summary>
    Clean,

    /// <summary>The file was walked successfully and audio is far from its video.</summary>
    Broken,

    /// <summary>The check could not run (ffprobe missing, process failure, timeout, file not found, no video/audio, duration unknown). Treat as "unknown", not "fine".</summary>
    Indeterminate,
}

/// <summary>
/// Result of an interleave check. For <see cref="InterleaveStatus.Broken"/> carries the measured
/// offset in seconds; otherwise the offset is 0.
/// </summary>
/// <param name="Status">The verdict.</param>
/// <param name="OffsetSeconds">Measured audio/video distance when Broken; 0 otherwise.</param>
public readonly record struct InterleaveResult(InterleaveStatus Status, double OffsetSeconds)
{
    /// <summary>Gets a clean verdict with offset 0.</summary>
    public static InterleaveResult Clean { get; } = new(InterleaveStatus.Clean, 0);

    /// <summary>Gets an indeterminate verdict with offset 0.</summary>
    public static InterleaveResult Indeterminate { get; } = new(InterleaveStatus.Indeterminate, 0);

    /// <summary>Builds a broken verdict carrying the measured offset.</summary>
    /// <param name="offsetSeconds">Measured audio/video distance in seconds.</param>
    /// <returns>A broken result.</returns>
    public static InterleaveResult Broken(double offsetSeconds) => new(InterleaveStatus.Broken, offsetSeconds);
}
