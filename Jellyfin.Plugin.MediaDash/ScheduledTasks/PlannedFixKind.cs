namespace Jellyfin.Plugin.MediaDash.ScheduledTasks;

/// <summary>
/// Shape of a planned fix operation. Determines which fixer path the executor takes:
/// <list type="bullet">
///   <item><see cref="Single"/> — hand the primary to the first <c>IFixer.CanFix</c>-matching fixer.</item>
///   <item><see cref="TrackPair"/> — <c>AudioLanguage</c> + <c>SubtitleLanguage</c> pair on the same file,
///     resolved by <c>TrackFixer.FixCombinedAsync</c> (one ffmpeg pass, both categories dropped).</item>
///   <item><see cref="TranscodeWithTracks"/> — <c>Quality</c>/<c>HeavyTranscode</c>/<c>FailedTranscode</c>
///     primary with co-queued <c>AudioLanguage</c>/<c>SubtitleLanguage</c> companions.
///     TranscodeFixer's language allow-lists drop the unwanted tracks incidentally during re-encode.</item>
/// </list>
/// </summary>
internal enum PlannedFixKind
{
    /// <summary>Single issue, no companions.</summary>
    Single,

    /// <summary><c>AudioLanguage</c> + <c>SubtitleLanguage</c> pair on the same file.</summary>
    TrackPair,

    /// <summary>Transcode-family primary + <c>AudioLanguage</c>/<c>SubtitleLanguage</c> companions.</summary>
    TranscodeWithTracks,
}
