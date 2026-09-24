namespace Jellyfin.Plugin.MediaDash.Fixers;

/// <summary>
/// Description of a verified-output replacement that <see cref="VerifiedReplacementService"/>
/// is asked to commit. Every field is caller-computed BEFORE the service runs — the service
/// makes no policy decisions of its own. See <c>artifacts/refactor/03A/protocol-table.md</c>
/// for the per-caller flow this replaces.
/// </summary>
/// <param name="SourcePath">The current original file, still present at request time. Will be
/// disposed per <paramref name="RecycleOriginal"/> after the verified output is promoted.</param>
/// <param name="OutputPath">The verified output produced by the caller (ffmpeg tempPath, repair
/// rung output, cover-strip remux). The service promotes this file — the caller must not
/// touch it after handing it in.</param>
/// <param name="DestinationPath">Where the committed file must end up. May equal
/// <paramref name="SourcePath"/> (in-place replacement — TrackFixer, EmbeddedCoverArtFixer,
/// same-container TranscodeFixer) or differ (container change on TranscodeFixer, extension
/// change on Playability rungs 3+4). A preexisting file at a non-equal destination is a
/// collision and the service refuses.</param>
/// <param name="ExpectedSourceLength">Length of <paramref name="SourcePath"/> captured BEFORE
/// the caller produced <paramref name="OutputPath"/>. The service rechecks the length
/// immediately before promoting so a source that mutated during the encode is refused
/// (<see cref="FixReason.SourceChanged"/>). Length + mtime is a useful stale-source guard,
/// not cryptographic identity — a hostile same-size/mtime swap is out of scope; document
/// limits rather than claim race freedom.</param>
/// <param name="ExpectedSourceMtimeUtcTicks">Last-write-time-UTC ticks of
/// <paramref name="SourcePath"/> captured BEFORE the caller produced <paramref name="OutputPath"/>.
/// Rechecked with <paramref name="ExpectedSourceLength"/>.</param>
/// <param name="RecycleOriginal">When true, dispose the source via <see cref="RecycleBin"/>
/// so the file remains restorable. When false, permanent-delete the source. PlayabilityFixer
/// always passes <c>true</c> (deliberate — the repaired original is always recycled,
/// regardless of the general failed-repair disposal setting); other fixers pass the
/// config-derived value for the issue type.</param>
/// <param name="PreserveSourceTimestamps">When true, restore the source's CreationTime and
/// LastWriteTime onto the committed destination file. Every current caller wants this
/// (F-202 / issue #31): Jellyfin's Recently Added widget sorts by DateModified, so a fixer
/// that bumps the stamps treats every housekeeping rewrite as "newly added" media.</param>
internal sealed record ReplacementRequest(
    string SourcePath,
    string OutputPath,
    string DestinationPath,
    long ExpectedSourceLength,
    long ExpectedSourceMtimeUtcTicks,
    bool RecycleOriginal,
    bool PreserveSourceTimestamps = true);
